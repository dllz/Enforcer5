using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Models;
using StackExchange.Redis;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Who the bot muted in each group (rollout step 16): PostgreSQL (<see cref="PostgresMuteRepository"/>), with changes
    /// queued in Redis while it is down, as the tempban schedule's are.
    /// <list type="bullet">
    /// <item>Each group's old Redis keys (<see cref="LegacyMutes"/>) are imported once, on the group's first change or
    /// list, before anything else is written for it, so an old entry can never undo a later change.</item>
    /// <item>A change is written at once, or, while PostgreSQL is down or slower than <see cref="Timeout"/>, goes to
    /// <c>mutes:queue:{edition}</c> and is written in order once it is back (every change is idempotent). While anything
    /// is queued, new changes queue behind it. The enforcement queue records each mute here, so it waits at most
    /// <see cref="Timeout"/>.</item>
    /// <item>The lists (/mutelist, /listmutedjoiners, /unmutenewjoiners) are read from PostgreSQL after the queue is
    /// written; while it is down they fail and say so.</item>
    /// </list>
    /// Nothing writes the old keys any more.
    /// </summary>
    internal sealed class MuteStore : IMuteRepository
    {
        internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

        internal static readonly TimeSpan DrainEvery = TimeSpan.FromSeconds(5);

        private readonly PostgresMuteRepository _store;
        private readonly LegacyMutes _legacy;
        private readonly Func<IDatabaseAsync> _redis;
        private readonly Func<bool> _ready;
        private readonly object _gate = new();
        private readonly SemaphoreSlim _draining = new(1, 1);
        // Groups known to be imported, so each costs one look at storage_imports per process.
        private readonly ConcurrentDictionary<long, byte> _imported = new();
        private int _queued;
        private long _sequence;

        internal MuteStore(PostgresMuteRepository store, LegacyMutes legacy, Func<IDatabaseAsync> redis, string edition, Func<bool> ready)
        {
            _store = store;
            _legacy = legacy;
            _redis = redis;
            _ready = ready;
            QueueKey = $"mutes:queue:{edition}";
        }

        internal string QueueKey { get; }

        internal int Queued => Volatile.Read(ref _queued);

        /// <summary>A change waiting in the queue.</summary>
        internal sealed record Change(string Id, long Sequence, string Kind, long ChatId, long UserId, long Until, long Now);

        // ------------------------------------------------------------------ changes

        public Task MarkMutedAsync(long chatId, long userId) => ChangeAsync("mute", chatId, userId);

        public Task MarkTempMutedAsync(long chatId, long userId, DateTime untilUtc) =>
            ChangeAsync("tempmute", chatId, userId, PostgresTempbanRepository.Second(untilUtc).ToUnixTimeSeconds());

        public Task MarkJoinerMutedAsync(long chatId, long userId) => ChangeAsync("joiner", chatId, userId);

        public Task ClearAsync(long chatId, long userId) => ChangeAsync("clear", chatId, userId);

        private async Task ChangeAsync(string kind, long chatId, long userId, long until = 0)
        {
            var change = new Change("mute:" + Guid.NewGuid().ToString("N"), NextSequence(), kind, chatId, userId, until,
                PostgresTempbanRepository.Second(Clock.UtcNow).ToUnixTimeSeconds());
            bool direct;
            lock (_gate)
            {
                direct = _queued == 0 && _ready();
                if (!direct) _queued++;
            }
            if (direct)
            {
                try
                {
                    await WithTimeout(ApplyAsync(change), $"Recording the {kind} of {userId} in {chatId}");
                    return;
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Recording the {kind} of {userId} in {chatId} in PostgreSQL; queued in Redis instead");
                    lock (_gate) _queued++;
                }
            }
            try
            {
                // Written before the caller hears back: the change must survive a restart.
                await _redis().HashSetAsync(QueueKey, change.Id, JsonSerializer.Serialize(change), When.Always, CommandFlags.None);
            }
            catch
            {
                lock (_gate) _queued--;
                throw;
            }
        }

        private async Task<bool> ApplyAsync(Change change)
        {
            await EnsureImportedAsync(change.ChatId);
            var now = DateTimeOffset.FromUnixTimeSeconds(change.Now).UtcDateTime;
            switch (change.Kind)
            {
                case "mute":
                    await _store.MuteAsync(change.ChatId, change.UserId, now);
                    break;
                case "tempmute":
                    await _store.TempMuteAsync(change.ChatId, change.UserId, DateTimeOffset.FromUnixTimeSeconds(change.Until).UtcDateTime, now);
                    break;
                case "joiner":
                    await _store.JoinerAsync(change.ChatId, change.UserId, now);
                    break;
                case "clear":
                    await _store.ClearAsync(change.ChatId, change.UserId);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(change), change.Kind, null);
            }
            return true;
        }

        /// <summary>Imports the group's old keys, once ever (<c>storage_imports</c>), before anything is written for it.</summary>
        private async Task EnsureImportedAsync(long chatId)
        {
            if (_imported.ContainsKey(chatId)) return;
            if (!await _store.ImportedAsync(chatId))
            {
                var mutes = await _legacy.ReadAsync(chatId, Clock.UtcNow);
                var imported = await _store.ImportAsync(chatId, mutes, Clock.UtcNow);
                if (imported > 0) LogHelper.Info($"Mutes of {chatId}: imported {imported} from Redis");
            }
            _imported[chatId] = 0;
        }

        // ------------------------------------------------------------------ reads

        public async Task<IReadOnlyList<long>> GetMutedAsync(long chatId) =>
            await (await ReadyForAsync(chatId)).MembersAsync(chatId, "mute");

        public async Task<IReadOnlyList<long>> GetMutedJoinersAsync(long chatId) =>
            await (await ReadyForAsync(chatId)).MembersAsync(chatId, "joiner");

        public async Task<IReadOnlyList<TimedRestriction>> GetTempMutedAsync(long chatId, DateTime nowUtc) =>
            await (await ReadyForAsync(chatId)).TempMutedAsync(chatId, nowUtc);

        private async Task<PostgresMuteRepository> ReadyForAsync(long chatId)
        {
            await DrainAsync();
            if (!_ready() || Queued > 0)
                throw new InvalidOperationException("The mute list is in PostgreSQL, which is not reachable now.");
            await EnsureImportedAsync(chatId);
            return _store;
        }

        // ------------------------------------------------------------------ the queue

        /// <summary>How many changes wait from before a restart. Throws when Redis fails.</summary>
        internal async Task LoadQueueAsync()
        {
            var waiting = (await ReadQueueAsync()).Count;
            lock (_gate) _queued = waiting;
            if (waiting > 0) LogHelper.Info($"Mutes: {waiting} changes queued from before the restart");
        }

        /// <summary>Writes the queued changes in order once PostgreSQL is there; stops at the first failure. Never throws.</summary>
        internal async Task DrainAsync()
        {
            if (Queued == 0 || !_ready()) return;
            if (!await _draining.WaitAsync(0)) return;
            try
            {
                foreach (var change in await ReadQueueAsync())
                {
                    await ApplyAsync(change);
                    await _redis().HashDeleteAsync(QueueKey, change.Id, CommandFlags.None);
                    lock (_gate) _queued = Math.Max(0, _queued - 1);
                }
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Writing queued mute changes to PostgreSQL ({Queued} waiting); trying again shortly");
            }
            finally
            {
                _draining.Release();
            }
        }

        /// <summary>Loads the queue from before a restart and keeps it drained, in the background. Never throws.</summary>
        internal void Start(CancellationToken stop) =>
            Bot.StartService(async ct =>
            {
                while (!ct.IsCancellationRequested)
                {
                    try
                    {
                        await LoadQueueAsync();
                        break;
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Report(e, "Reading the queued mute changes; trying again shortly");
                    }
                    await Clock.WaitAsync(TimeSpan.FromSeconds(10), ct);
                }
                while (!ct.IsCancellationRequested)
                {
                    await DrainAsync();
                    await Clock.WaitAsync(DrainEvery, ct);
                }
            }, stop, "MuteStore");

        private async Task<List<Change>> ReadQueueAsync()
        {
            var list = new List<Change>();
            foreach (var entry in await _redis().HashGetAllAsync(QueueKey, CommandFlags.None) ?? Array.Empty<HashEntry>())
            {
                try
                {
                    var change = JsonSerializer.Deserialize<Change>(entry.Value.ToString());
                    if (change?.Id != null) list.Add(change);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Reading queued mute change {entry.Name}, dropped");
                    await _redis().HashDeleteAsync(QueueKey, entry.Name, CommandFlags.None);
                }
            }
            return list.OrderBy(c => c.Sequence).ToList();
        }

        private long NextSequence()
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

        /// <summary>
        /// Forgets that the group was imported (Helpers/GroupMigration: its new id may have been looked at before the
        /// move).
        /// </summary>
        internal void Forget(long chatId) => _imported.TryRemove(chatId, out _);

        /// <summary>Forgets the state in memory, for tests (the queue in Redis and PostgreSQL stay).</summary>
        internal void ResetForTests()
        {
            lock (_gate) _queued = 0;
            _imported.Clear();
        }
    }
}
