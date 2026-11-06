using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Storage;
using StackExchange.Redis;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// The lists that used to be Redis sets (rollout step 14, <see cref="RoleList"/>): who is authorized, a moderator,
    /// blocked from elevating, exempt from the checks, off @admin reports, blocked from reporting or on /pingall's list
    /// in each group, and the language admins, premium groups and ignored chats. PostgreSQL is the store
    /// (<see cref="PostgresRoleRepository"/>); every entry is kept in memory, since there are few and some are read on
    /// every message.
    /// <list type="bullet">
    /// <item>Loaded once PostgreSQL is ready, reloaded every minute and as soon as the other edition changed something
    /// (<c>roles:version</c> in Redis, checked every 5 s).</item>
    /// <item>Each group's old Redis sets are imported on its first use (a dispatched import; until it ran, the group's
    /// lists are read from the old sets, as before); the global ones once the lists are loaded.</item>
    /// <item>While PostgreSQL is down, or a change takes over 3 s, the change goes to <c>roles:queue:{edition}</c> and is
    /// written in order once it is back (changes are idempotent); it counts in memory straight away.</item>
    /// </list>
    /// Nothing writes the old Redis sets any more; a binary rollback finds them as they were at the deploy.
    /// </summary>
    internal static class Roles
    {
        internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);
        internal static readonly TimeSpan ReloadEvery = TimeSpan.FromMinutes(1);
        internal static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(5);
        internal const string VersionKey = "roles:version";

        internal static PostgresRoleRepository Repository { get; set; } = new(() => PostgresStorage.Database);

        /// <summary>Whether PostgreSQL can be used; settable for tests.</summary>
        internal static Func<bool> Ready { get; set; } = () => PostgresStorage.Ready;

        internal static string QueueKey => $"roles:queue:{BanHistory.Edition}";

        private sealed record Snapshot(IReadOnlyDictionary<(long Chat, RoleList List), HashSet<long>> Lists, HashSet<long> Imported);

        /// <summary>A change waiting in the queue.</summary>
        internal sealed record Change(string Id, long Sequence, long ChatId, RoleList List, long TargetId, bool On, long? ActorId, DateTimeOffset At);

        private static readonly object Gate = new();
        private static volatile Snapshot _snapshot;
        private static long _changes;
        private static int _queued;
        private static long _sequence;
        private static RedisValue _version = RedisValue.Null;
        private static DateTime _loadedAt;
        private static readonly ConcurrentDictionary<long, Task> Importing = new();
        private static readonly SemaphoreSlim Draining = new(1, 1);

        /// <summary>True once the lists were loaded from PostgreSQL.</summary>
        internal static bool Loaded => _snapshot != null;

        internal static int Queued => Volatile.Read(ref _queued);

        private static long Scope(long chatId, RoleList list) => list.IsGlobal() ? 0 : chatId;

        /// <summary>The lists of this group (or the global ones) come from memory.</summary>
        private static bool Known(long scope) => _snapshot is { } s && s.Imported.Contains(scope);

        // ------------------------------------------------------------------ reads

        /// <summary>Whether <paramref name="id"/> is on the list (in <paramref name="chatId"/> for a group's list).</summary>
        internal static async Task<bool> ContainsAsync(long chatId, RoleList list, long id)
        {
            var scope = Scope(chatId, list);
            if (Known(scope))
                return _snapshot.Lists.TryGetValue((scope, list), out var set) && set.Contains(id);
            StartImport(scope);
            return await Redis.db.SetContainsAsync(RoleLists.LegacyKey(scope, list), id);
        }

        internal static bool Contains(long chatId, RoleList list, long id) => ContainsAsync(chatId, list, id).GetAwaiter().GetResult();

        /// <summary>Everyone on the list.</summary>
        internal static async Task<IReadOnlyCollection<long>> MembersAsync(long chatId, RoleList list)
        {
            var scope = Scope(chatId, list);
            if (Known(scope))
                return _snapshot.Lists.TryGetValue((scope, list), out var set) ? set.ToArray() : Array.Empty<long>();
            StartImport(scope);
            return Parse(await Redis.db.SetMembersAsync(RoleLists.LegacyKey(scope, list)));
        }

        internal static IReadOnlyCollection<long> Members(long chatId, RoleList list) => MembersAsync(chatId, list).GetAwaiter().GetResult();

        private static long[] Parse(RedisValue[] values) =>
            (values ?? Array.Empty<RedisValue>()).Select(v => long.TryParse(v.ToString(), out var id) ? id : (long?)null)
                .Where(id => id.HasValue).Select(id => id.Value).Distinct().ToArray();

        // ------------------------------------------------------------------ changes

        /// <summary>
        /// Puts <paramref name="id"/> on the list or takes it off, as asked by <paramref name="actorId"/>. Counts at once;
        /// written to PostgreSQL, or queued when it cannot be. Throws only when even the queue cannot be written.
        /// </summary>
        internal static async Task SetAsync(long chatId, RoleList list, long id, bool on, long? actorId = null)
        {
            var scope = Scope(chatId, list);
            var change = new Change("role:" + Guid.NewGuid().ToString("N"), NextSequence(), scope, list, id, on, actorId,
                new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero));
            bool direct;
            lock (Gate)
            {
                direct = _queued == 0 && Loaded && Ready();
                if (!direct) _queued++;
            }
            if (direct)
            {
                try
                {
                    await WithTimeout(WriteAsync(change), $"Changing {list} of {id} in {scope}");
                    ApplyInMemory(change);
                    return;
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Changing {list} of {id} in {scope} in PostgreSQL; queued in Redis instead");
                    lock (Gate) _queued++;
                }
            }
            try
            {
                await Redis.db.HashSetAsync(QueueKey, change.Id, JsonSerializer.Serialize(change));
            }
            catch
            {
                lock (Gate) _queued--;
                throw;
            }
            ApplyInMemory(change);
        }

        internal static void Set(long chatId, RoleList list, long id, bool on, long? actorId = null) =>
            SetAsync(chatId, list, id, on, actorId).GetAwaiter().GetResult();

        /// <summary>The group's old sets imported first, so the import cannot undo this change.</summary>
        private static async Task<bool> WriteAsync(Change change)
        {
            await ImportAsync(change.ChatId);
            await Repository.SetAsync(new RoleEntry(change.ChatId, change.List, change.TargetId), change.On, change.ActorId, change.At);
            _ = Redis.db.StringIncrementAsync(VersionKey, 1, CommandFlags.FireAndForget);
            return true;
        }

        private static void ApplyInMemory(Change change)
        {
            lock (Gate)
            {
                _changes++;
                if (_snapshot is not { } current || !current.Imported.Contains(change.ChatId)) return;
                var lists = current.Lists.ToDictionary(p => p.Key, p => p.Value);
                var key = (change.ChatId, change.List);
                var set = lists.TryGetValue(key, out var had) ? new HashSet<long>(had) : new HashSet<long>();
                if (change.On) set.Add(change.TargetId);
                else set.Remove(change.TargetId);
                lists[key] = set;
                _snapshot = current with { Lists = lists };
            }
        }

        // ------------------------------------------------------------------ loading and importing

        /// <summary>Loads every list from PostgreSQL, unless something changed here meanwhile. Throws when the read fails.</summary>
        internal static async Task LoadAsync()
        {
            if (await ReadAllAsync() && !Known(0)) await ImportAsync(0);
        }

        /// <summary>Replaces what is in memory with PostgreSQL's lists; false when a change here got in the way.</summary>
        private static async Task<bool> ReadAllAsync()
        {
            long changes;
            lock (Gate) changes = _changes;
            var version = await Redis.db.StringGetAsync(VersionKey);
            var (entries, imported) = await Repository.LoadAsync();
            lock (Gate)
            {
                // A change made while reading, or waiting in the queue, is in memory but maybe not in what was read.
                if (_snapshot != null && (_changes != changes || _queued > 0)) return false;
                var lists = entries.GroupBy(e => (e.ChatId, e.List)).ToDictionary(g => g.Key, g => g.Select(e => e.Id).ToHashSet());
                _snapshot = new Snapshot(lists, imported.ToHashSet());
                _version = version;
                _loadedAt = Clock.UtcNow;
                return true;
            }
        }

        /// <summary>Starts the import of a group's old sets in the background, once, when the lists are loaded.</summary>
        private static void StartImport(long scope)
        {
            if (!Loaded || !Ready() || Importing.ContainsKey(scope)) return;
            var started = new TaskCompletionSource();
            if (!Importing.TryAdd(scope, started.Task)) return;
            Bot.Dispatch(async () =>
            {
                try
                {
                    await ImportAsync(scope);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Importing the role lists of {scope}; read from Redis until it works");
                }
                finally
                {
                    Importing.TryRemove(scope, out _);
                    started.TrySetResult();
                }
            }, "Roles.Import");
        }

        /// <summary>Imports the old sets of a group (or the global ones, 0) unless they were. Throws when it fails.</summary>
        internal static async Task ImportAsync(long scope)
        {
            if (Known(scope)) return;
            var lists = scope == 0 ? RoleLists.GlobalLists : RoleLists.GroupLists;
            var entries = new List<RoleEntry>();
            foreach (var list in lists)
                entries.AddRange(Parse(await Redis.db.SetMembersAsync(RoleLists.LegacyKey(scope, list))).Select(id => new RoleEntry(scope, list, id)));
            var ours = await Repository.ImportAsync(scope, entries, new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero));
            if (!ours)
            {
                // The other edition imported it: what it wrote is read with everything else (now, or at the next reload
                // if a change here got in the way; until then the group is read from the old sets).
                await ReadAllAsync();
                return;
            }
            if (entries.Count > 0) LogHelper.Info($"Roles: imported {entries.Count} entries of {(scope == 0 ? "the global lists" : scope.ToString())}");
            lock (Gate)
            {
                _changes++;
                if (_snapshot is not { } current) return;
                var lists2 = current.Lists.ToDictionary(p => p.Key, p => p.Value);
                foreach (var group in entries.GroupBy(e => (e.ChatId, e.List)))
                    lists2[group.Key] = (lists2.TryGetValue(group.Key, out var had) ? had : Enumerable.Empty<long>())
                        .Concat(group.Select(e => e.Id)).ToHashSet();
                var imported = new HashSet<long>(current.Imported) { scope };
                _snapshot = new Snapshot(lists2, imported);
            }
            _ = Redis.db.StringIncrementAsync(VersionKey, 1, CommandFlags.FireAndForget);
        }

        // ------------------------------------------------------------------ the queue

        /// <summary>Writes the queued changes in order once PostgreSQL is there; stops at the first failure. Never throws.</summary>
        internal static async Task DrainAsync()
        {
            if (Queued == 0 || !Loaded || !Ready()) return;
            if (!await Draining.WaitAsync(0)) return;
            try
            {
                foreach (var change in await ReadQueueAsync())
                {
                    await WriteAsync(change);
                    await Redis.db.HashDeleteAsync(QueueKey, change.Id);
                    lock (Gate) _queued = Math.Max(0, _queued - 1);
                }
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Writing queued role changes to PostgreSQL ({Queued} waiting); trying again shortly");
            }
            finally
            {
                Draining.Release();
            }
        }

        private static async Task<List<Change>> ReadQueueAsync()
        {
            var list = new List<Change>();
            foreach (var entry in await Redis.db.HashGetAllAsync(QueueKey) ?? Array.Empty<HashEntry>())
            {
                try
                {
                    var change = JsonSerializer.Deserialize<Change>(entry.Value.ToString());
                    if (change?.Id != null) list.Add(change);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Reading queued role change {entry.Name}, dropped");
                    await Redis.db.HashDeleteAsync(QueueKey, entry.Name);
                }
            }
            return list.OrderBy(c => c.Sequence).ToList();
        }

        /// <summary>
        /// At startup: how many changes wait, then the lists once PostgreSQL is ready; then keeps them current and the
        /// queue drained. Never throws.
        /// </summary>
        internal static void Start(CancellationToken stop) =>
            Bot.StartService(async ct =>
            {
                var waiting = (await ReadQueueAsync()).Count;
                lock (Gate) _queued = waiting;
                var reported = false;
                while (!ct.IsCancellationRequested)
                {
                    if (Ready())
                    {
                        try
                        {
                            await DrainAsync();
                            if (!Loaded || Clock.UtcNow - _loadedAt >= ReloadEvery || await Redis.db.StringGetAsync(VersionKey) != _version)
                                await LoadAsync();
                            reported = false;
                        }
                        catch (Exception e)
                        {
                            if (!reported) ErrorReporter.Report(e, Loaded
                                ? "Reloading the role lists; the last loaded ones stay in use"
                                : "Loading the role lists; they are read from the old Redis sets until this works");
                            reported = true;
                        }
                    }
                    await Clock.WaitAsync(CheckEvery, ct);
                }
            }, stop, "Roles");

        // ------------------------------------------------------------------ helpers

        private static long NextSequence()
        {
            var now = Clock.UtcNow.Ticks;
            while (true)
            {
                var last = Interlocked.Read(ref _sequence);
                var next = Math.Max(last + 1, now);
                if (Interlocked.CompareExchange(ref _sequence, next, last) == last) return next;
            }
        }

        // Real time, not Clock: it bounds how long PostgreSQL may take (see WarnStore).
        private static async Task WithTimeout(Task<bool> task, string what)
        {
            if (await Task.WhenAny(task, Task.Delay(Timeout)) == task)
            {
                await task;
                return;
            }
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new TimeoutException($"{what} took longer than {Timeout.TotalSeconds:0.#} s");
        }

        /// <summary>Forgets everything in memory, for tests.</summary>
        internal static void ResetForTests()
        {
            lock (Gate)
            {
                _snapshot = null;
                _queued = 0;
                _changes++;
                _version = RedisValue.Null;
                Importing.Clear();
            }
        }
    }
}
