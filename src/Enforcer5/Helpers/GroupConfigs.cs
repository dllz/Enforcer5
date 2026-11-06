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
    /// Each group's <see cref="GroupConfig"/> (rollout step 15): its settings other than the automatic checks', its
    /// welcome, rules, about, language, link, log channel and extras. PostgreSQL is the store
    /// (<see cref="PostgresGroupConfigRepository"/>); Redis only says when it changed (<c>config:version:{chat}</c>,
    /// compared at most every <see cref="RecheckAfter"/>, which is how the other edition's changes arrive) and queues
    /// changes while PostgreSQL is down (<c>config:queue:{edition}</c>).
    /// <list type="bullet">
    /// <item>A group in memory is served from memory. One that is not waits up to <see cref="ColdWait"/> for PostgreSQL
    /// (a read, or on the group's first use its detection settings and then the old Redis keys imported,
    /// <see cref="LegacyGroupConfig"/>); past that, or with PostgreSQL down, it is served what the old keys say until the
    /// load finishes.</item>
    /// <item>A change counts at once in memory, and is written to PostgreSQL, or queued when it cannot be (down, slower
    /// than <see cref="Timeout"/>, or not imported yet) and written in order once it can be.</item>
    /// </list>
    /// Nothing writes the old keys any more.
    /// </summary>
    internal static class GroupConfigs
    {
        internal static TimeSpan RecheckAfter = TimeSpan.FromSeconds(10);
        internal static TimeSpan ReloadAfter = TimeSpan.FromMinutes(5);
        internal static TimeSpan ColdWait = TimeSpan.FromSeconds(2);
        internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);
        internal static readonly TimeSpan DrainEvery = TimeSpan.FromSeconds(5);
        private const int MaxGroups = 50_000;

        internal static PostgresGroupConfigRepository Repository => Repositories.GroupConfigTable;

        internal static Func<bool> Ready { get; set; } = () => PostgresStorage.Ready;

        internal static string VersionKey(long chatId) => $"config:version:{chatId}";
        internal static string QueueKey => $"config:queue:{BanHistory.Edition}";

        private sealed class Entry
        {
            internal GroupConfig Config;
            /// <summary>From the old keys, until PostgreSQL has answered.</summary>
            internal bool Provisional;
            internal DateTime CheckedAt;
            internal DateTime LoadedAt;
        }

        /// <summary>A change waiting in the queue.</summary>
        internal sealed record Queued(string Id, long Sequence, long ChatId, ConfigChange Change);

        private static readonly ConcurrentDictionary<long, Entry> Entries = new();
        private static readonly ConcurrentDictionary<long, Task<GroupConfig>> Loading = new();
        private static readonly object Gate = new();
        // The queue in memory, in order (mirrors the Redis journal), so a load can lay it over what it read.
        private static readonly List<Queued> Pending = new();
        private static readonly SemaphoreSlim Draining = new(1, 1);
        private static long _sequence;

        internal static int QueuedCount { get { lock (Gate) return Pending.Count; } }

        // ------------------------------------------------------------------ reads

        /// <summary>The group's config for the message being handled. Throws only when Redis fails too.</summary>
        internal static async Task<GroupConfig> GetAsync(long chatId)
        {
            // Only groups have one; a private chat gets the defaults.
            if (chatId >= 0) return GroupConfig.NewGroup;
            var now = Clock.UtcNow;
            if (Entries.TryGetValue(chatId, out var entry))
            {
                if (now - entry.CheckedAt < RecheckAfter) return entry.Config;
                entry.CheckedAt = now;
                if (entry.Provisional || now - entry.LoadedAt >= ReloadAfter)
                {
                    _ = StartLoad(chatId);
                    return entry.Config;
                }
                try
                {
                    var stored = await Redis.db.StringGetAsync(VersionKey(chatId));
                    if (stored.TryParse(out long version) && version > entry.Config.Version) _ = StartLoad(chatId);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Checking the config version of {chatId}");
                }
                return entry.Config;
            }

            var load = StartLoad(chatId);
            if (load != null)
            {
                await Clock.WaitForAsync(load, ColdWait);
                if (load.IsCompletedSuccessfully && load.Result != null && Entries.TryGetValue(chatId, out entry)) return entry.Config;
            }
            var legacy = WithPending(chatId, await LegacyGroupConfig.ReadAsync(Redis.db, chatId));
            Remember(chatId, legacy, provisional: true);
            return Entries.TryGetValue(chatId, out entry) ? entry.Config : legacy;
        }

        /// <summary>For code that cannot await (commands, menus).</summary>
        internal static GroupConfig For(long chatId) => GetAsync(chatId).GetAwaiter().GetResult();

        // ------------------------------------------------------------------ changes

        /// <summary>
        /// Makes the change: at once in memory, and in PostgreSQL or the queue. Throws only when even the queue cannot
        /// be written.
        /// </summary>
        internal static async Task ChangeAsync(long chatId, ConfigChange change)
        {
            if (chatId >= 0) throw new ArgumentOutOfRangeException(nameof(chatId), chatId, "Only groups have a config");
            var current = await GetAsync(chatId);
            var queued = new Queued("config:" + Guid.NewGuid().ToString("N"), NextSequence(), chatId, change);
            bool direct;
            lock (Gate) direct = Pending.Count == 0 && Ready() && Entries.TryGetValue(chatId, out var e) && !e.Provisional;
            if (direct)
            {
                try
                {
                    var version = await WithTimeout(Repository.ApplyAsync(chatId, change), $"Changing the config of {chatId}");
                    Remember(chatId, change.ApplyTo(current) with { Version = version }, provisional: false, force: true);
                    _ = Redis.db.StringSetAsync(VersionKey(chatId), version, TimeSpan.FromDays(30), When.Always, CommandFlags.FireAndForget);
                    return;
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Changing the config of {chatId} in PostgreSQL; queued in Redis instead");
                }
            }
            await Redis.db.HashSetAsync(QueueKey, queued.Id, JsonSerializer.Serialize(queued));
            lock (Gate) Pending.Add(queued);
            if (Entries.TryGetValue(chatId, out var entry))
                Remember(chatId, change.ApplyTo(entry.Config), entry.Provisional, force: true);
        }

        internal static void Change(long chatId, ConfigChange change) => ChangeAsync(chatId, change).GetAwaiter().GetResult();

        /// <summary>As <see cref="Change"/>, reporting a failure; false when it failed (the admin is told).</summary>
        internal static bool TryChange(long chatId, ConfigChange change)
        {
            try
            {
                Change(chatId, change);
                return true;
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Changing the config of {chatId}");
                return false;
            }
        }

        // ------------------------------------------------------------------ loading

        private static Task<GroupConfig> StartLoad(long chatId)
        {
            if (!Ready()) return null;
            var done = new TaskCompletionSource<GroupConfig>(TaskCreationOptions.RunContinuationsAsynchronously);
            var running = Loading.GetOrAdd(chatId, done.Task);
            if (running != done.Task) return running;
            Bot.Dispatch(async () =>
            {
                GroupConfig loaded = null;
                try
                {
                    loaded = await LoadOrImportAsync(chatId);
                    // A change queued for the group lies on top until it is written.
                    Remember(chatId, WithPending(chatId, loaded), provisional: false);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Loading the config of {chatId}; the old keys or the last loaded config stay in use");
                }
                finally
                {
                    Loading.TryRemove(chatId, out _);
                    done.TrySetResult(loaded);
                }
            }, "GroupConfigs.Load");
            return done.Task;
        }

        private static async Task<GroupConfig> LoadOrImportAsync(long chatId)
        {
            var stored = await Repository.LoadAsync(chatId);
            if (stored != null) return stored;
            // The group_settings row is the detection settings' import marker: they come first.
            await Detection.DetectionSettings.EnsureImportedAsync(chatId);
            var legacy = await LegacyGroupConfig.ReadAsync(Redis.db, chatId);
            var imported = await Repository.ImportAsync(chatId, legacy);
            if (legacy.Extras.Count + legacy.Content.Count > 0)
                LogHelper.Info($"Config of {chatId} imported: {legacy.Content.Count} texts, {legacy.Extras.Count} extras");
            return imported;
        }

        private static GroupConfig WithPending(long chatId, GroupConfig config)
        {
            lock (Gate)
                foreach (var queued in Pending.Where(p => p.ChatId == chatId))
                    config = queued.Change.ApplyTo(config);
            return config;
        }

        private static void Remember(long chatId, GroupConfig config, bool provisional, bool force = false)
        {
            var now = Clock.UtcNow;
            if (Entries.Count > MaxGroups) Entries.Clear();
            Entries.AddOrUpdate(chatId,
                _ => new Entry { Config = config, Provisional = provisional, CheckedAt = now, LoadedAt = now },
                (_, current) =>
                {
                    if (!force && !current.Provisional && (provisional || current.Config.Version > config.Version)) return current;
                    return new Entry { Config = config, Provisional = provisional, CheckedAt = now, LoadedAt = provisional ? current.LoadedAt : now };
                });
        }

        // ------------------------------------------------------------------ the queue

        /// <summary>Writes the queued changes in order once PostgreSQL is there; stops at the first failure. Never throws.</summary>
        internal static async Task DrainAsync()
        {
            if (QueuedCount == 0 || !Ready()) return;
            if (!await Draining.WaitAsync(0)) return;
            try
            {
                while (true)
                {
                    Queued next;
                    lock (Gate) next = Pending.FirstOrDefault();
                    if (next == null) break;
                    // Imported first (the change lies on top of what is imported).
                    if (await Repository.LoadAsync(next.ChatId) == null) await LoadOrImportAsync(next.ChatId);
                    var version = await Repository.ApplyAsync(next.ChatId, next.Change);
                    await Redis.db.HashDeleteAsync(QueueKey, next.Id);
                    lock (Gate) Pending.Remove(next);
                    _ = Redis.db.StringSetAsync(VersionKey(next.ChatId), version, TimeSpan.FromDays(30), When.Always, CommandFlags.FireAndForget);
                    // Read again (with any changes still queued on top), so memory has the version.
                    bool more;
                    lock (Gate) more = Pending.Any(p => p.ChatId == next.ChatId);
                    if (!more) Entries.TryRemove(next.ChatId, out _);
                }
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Writing queued config changes to PostgreSQL ({QueuedCount} waiting); trying again shortly");
            }
            finally
            {
                Draining.Release();
            }
        }

        /// <summary>At startup: the changes queued before the restart, then keeps the queue drained. Never throws.</summary>
        internal static void Start(CancellationToken stop) =>
            Bot.StartService(async ct =>
            {
                await LoadQueueAsync();
                while (!ct.IsCancellationRequested)
                {
                    await DrainAsync();
                    await Clock.WaitAsync(DrainEvery, ct);
                }
            }, stop, "GroupConfigs");

        internal static async Task LoadQueueAsync()
        {
            var list = new List<Queued>();
            foreach (var entry in await Redis.db.HashGetAllAsync(QueueKey) ?? Array.Empty<HashEntry>())
            {
                try
                {
                    var queued = JsonSerializer.Deserialize<Queued>(entry.Value.ToString());
                    if (queued?.Id != null && queued.Change != null) list.Add(queued);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Reading queued config change {entry.Name}, dropped");
                    await Redis.db.HashDeleteAsync(QueueKey, entry.Name);
                }
            }
            lock (Gate)
            {
                Pending.Clear();
                Pending.AddRange(list.OrderBy(q => q.Sequence));
            }
            if (list.Count > 0) LogHelper.Info($"Config: {list.Count} changes queued from before the restart");
        }

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
        private static async Task<T> WithTimeout<T>(Task<T> task, string what)
        {
            if (await Task.WhenAny(task, Task.Delay(Timeout)) == task) return await task;
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new TimeoutException($"{what} took longer than {Timeout.TotalSeconds:0.#} s");
        }

        /// <summary>Drops what is in memory for the group, so the next read loads it again (Helpers/GroupMigration).</summary>
        internal static void Forget(long chatId)
        {
            Entries.TryRemove(chatId, out _);
            Loading.TryRemove(chatId, out _);
        }

        internal static void ResetForTests()
        {
            Entries.Clear();
            Loading.Clear();
            lock (Gate) Pending.Clear();
        }
    }
}
