using System;
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
    /// The warn counts the bot runs on (rollout step 12): PostgreSQL (<see cref="PostgresWarnRepository"/>), with
    /// changes queued in Redis while PostgreSQL is down (Daniel, 2026-11-06).
    /// <para>
    /// While PostgreSQL is down, or a change takes longer than <see cref="Timeout"/>, the change goes to a journal in
    /// Redis (<c>warns:queue:{edition}</c>) and is carried out once PostgreSQL is back, in order, by
    /// <see cref="DrainAsync"/>; until then the count it returns is an estimate from <c>warns:cache:{chat}</c> (the
    /// count after every change, written alongside) or the old Redis hashes. So warns, max warns and the media limit
    /// keep working through an outage. While anything is queued, new changes queue behind it, so they are carried out
    /// in the order they were made. A change carries an id, so one carried out twice (it timed out, then went through
    /// after all) counts once.
    /// </para>
    /// </summary>
    internal sealed class WarnStore : IWarnRepository
    {
        /// <summary>How long a change waits for PostgreSQL before it is queued instead.</summary>
        internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

        /// <summary>How long a count is kept in the Redis cache after its last change.</summary>
        internal static readonly TimeSpan CacheFor = TimeSpan.FromDays(90);

        /// <summary>How often the queue is tried while it holds anything.</summary>
        internal static readonly TimeSpan DrainEvery = TimeSpan.FromSeconds(5);

        private readonly IWarnRepository _store;
        private readonly Func<IDatabaseAsync> _redis;
        private readonly Func<bool> _ready;
        private readonly LegacyWarnCounts _legacy;
        private readonly object _gate = new();
        private readonly SemaphoreSlim _draining = new(1, 1);
        private int _queued;
        private long _sequence;

        internal WarnStore(IWarnRepository store, Func<IDatabaseAsync> redis, string edition, Func<bool> ready)
        {
            _store = store;
            _redis = redis;
            _ready = ready;
            _legacy = new LegacyWarnCounts(redis);
            QueueKey = $"warns:queue:{edition}";
        }

        internal string QueueKey { get; }

        /// <summary>Changes waiting in the queue.</summary>
        internal int Queued => Volatile.Read(ref _queued);

        internal static string CacheKey(long chatId) => $"warns:cache:{chatId}";

        internal static string CacheField(WarnKind kind, long userId) => $"{(int)kind}:{userId}";

        // ------------------------------------------------------------------ changes

        public async Task<WarnChanged> ApplyAsync(WarnOp op)
        {
            op = op with
            {
                Id = op.Id ?? "warn:" + Guid.NewGuid().ToString("N"),
                At = op.At == default ? new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero) : op.At,
                Sequence = op.Sequence != 0 ? op.Sequence : NextSequence()
            };

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
                    var done = await WithTimeout(_store.ApplyAsync(op), $"Changing warns of {op.UserId} in {op.ChatId}");
                    Cache(op.ChatId, op.Kind, op.UserId, done.After, done.Lifetime);
                    return done;
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Changing warns of {op.UserId} in {op.ChatId} in PostgreSQL; queued in Redis instead");
                    lock (_gate) _queued++;
                }
            }

            try
            {
                return await QueueAsync(op);
            }
            catch
            {
                lock (_gate) _queued--;
                throw;
            }
        }

        private async Task<WarnChanged> QueueAsync(WarnOp op)
        {
            var (before, lifetime) = await EstimateAsync(op.ChatId, op.Kind, op.UserId);
            var after = PostgresWarnRepository.After(op.Operation, before, op.Quantity);
            lifetime = PostgresWarnRepository.LifetimeAfter(op.Operation, lifetime, before, after);
            // Written before the caller hears back: the change must survive a restart.
            await _redis().HashSetAsync(QueueKey, op.Id, JsonSerializer.Serialize(op), When.Always, CommandFlags.None);
            Cache(op.ChatId, op.Kind, op.UserId, after, lifetime);
            return new WarnChanged(before, after, lifetime, null) { Queued = true };
        }

        public async Task SetLifetimeAsync(long chatId, long userId, long lifetime)
        {
            // A data recovery, which an admin can try again: no queue.
            if (!Direct) throw new InvalidOperationException("Warns are waiting for PostgreSQL; try again later.");
            await WithTimeout(Done(_store.SetLifetimeAsync(chatId, userId, lifetime)), $"Setting the lifetime warns of {userId} in {chatId}");

            static async Task<bool> Done(Task task)
            {
                await task;
                return true;
            }
        }

        // ------------------------------------------------------------------ reads

        public async Task<long> GetCountAsync(long chatId, WarnKind kind, long userId)
        {
            if (Direct)
            {
                try
                {
                    return await WithTimeout(_store.GetCountAsync(chatId, kind, userId), $"Reading warns of {userId} in {chatId}");
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Reading warns of {userId} in {chatId} from PostgreSQL; the cached count is used");
                }
            }
            return (await EstimateAsync(chatId, kind, userId)).Active;
        }

        public async Task<WarnCounts> GetCountsAsync(long chatId, long userId)
        {
            if (Direct)
            {
                try
                {
                    return await WithTimeout(_store.GetCountsAsync(chatId, userId), $"Reading warns of {userId} in {chatId}");
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Reading warns of {userId} in {chatId} from PostgreSQL; the cached counts are used");
                }
            }
            var warns = await EstimateAsync(chatId, WarnKind.Warn, userId);
            return new WarnCounts(warns.Active, (await EstimateAsync(chatId, WarnKind.Prewarn, userId)).Active,
                (await EstimateAsync(chatId, WarnKind.Media, userId)).Active, warns.Lifetime);
        }

        private bool Direct
        {
            get { lock (_gate) return _queued == 0 && _ready(); }
        }

        // ------------------------------------------------------------------ the queue

        /// <summary>At startup: how many changes wait from before the restart.</summary>
        internal async Task LoadAsync()
        {
            var waiting = (await ReadQueueAsync()).Count;
            lock (_gate) _queued = waiting;
            if (waiting > 0) LogHelper.Info($"Warns: {waiting} changes queued from before the restart");
        }

        /// <summary>
        /// Carries out the queued changes in order, once PostgreSQL is there. Stops at the first that fails and tries
        /// again next time. Never throws.
        /// </summary>
        internal async Task DrainAsync()
        {
            if (Queued == 0 || !_ready()) return;
            if (!await _draining.WaitAsync(0)) return;
            try
            {
                var done = 0;
                foreach (var op in await ReadQueueAsync())
                {
                    var result = await _store.ApplyAsync(op);
                    Cache(op.ChatId, op.Kind, op.UserId, result.After, result.Lifetime);
                    await _redis().HashDeleteAsync(QueueKey, op.Id, CommandFlags.None);
                    lock (_gate) _queued = Math.Max(0, _queued - 1);
                    done++;
                }
                if (done > 0) LogHelper.Info($"Warns: {done} queued changes written to PostgreSQL");
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Writing queued warn changes to PostgreSQL ({Queued} waiting); trying again shortly");
            }
            finally
            {
                _draining.Release();
            }
        }

        /// <summary>Loads the queue and keeps draining it, in the background. Never throws.</summary>
        internal void Start(CancellationToken stop) =>
            Bot.StartService(async ct =>
            {
                await LoadAsync();
                while (!ct.IsCancellationRequested)
                {
                    await DrainAsync();
                    await Clock.WaitAsync(DrainEvery, ct);
                }
            }, stop, "WarnQueue");

        private async Task<List<WarnOp>> ReadQueueAsync()
        {
            var list = new List<WarnOp>();
            foreach (var entry in await _redis().HashGetAllAsync(QueueKey, CommandFlags.None) ?? Array.Empty<HashEntry>())
            {
                try
                {
                    var op = JsonSerializer.Deserialize<WarnOp>(entry.Value.ToString());
                    if (op?.Id != null) list.Add(op);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Reading queued warn change {entry.Name}, dropped");
                    await _redis().HashDeleteAsync(QueueKey, entry.Name, CommandFlags.None);
                }
            }
            return list.OrderBy(o => o.Sequence).ToList();
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>The count and lifetime count while PostgreSQL cannot say: the last one seen, else the old hashes.</summary>
        private async Task<(long Active, long Lifetime)> EstimateAsync(long chatId, WarnKind kind, long userId)
        {
            var cached = await _redis().HashGetAsync(CacheKey(chatId), CacheField(kind, userId), CommandFlags.None);
            var parts = cached.IsNullOrEmpty ? null : cached.ToString().Split(':');
            if (parts?.Length == 2 && long.TryParse(parts[0], out var active) && long.TryParse(parts[1], out var lifetime))
                return (active, lifetime);
            return await _legacy.ReadAsync(chatId, kind, userId);
        }

        private void Cache(long chatId, WarnKind kind, long userId, long active, long lifetime)
        {
            var db = _redis();
            _ = db.HashSetAsync(CacheKey(chatId), CacheField(kind, userId), $"{active}:{lifetime}", When.Always, CommandFlags.FireAndForget);
            _ = db.KeyExpireAsync(CacheKey(chatId), CacheFor, CommandFlags.FireAndForget);
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

        /// <summary>
        /// The task's result, or a <see cref="TimeoutException"/> after <see cref="Timeout"/>. Real time, not
        /// <see cref="Clock"/>: it bounds how long PostgreSQL may take, and in the harness the simulated clock would
        /// jump over a query in progress.
        /// </summary>
        private static async Task<T> WithTimeout<T>(Task<T> task, string what)
        {
            if (await Task.WhenAny(task, Task.Delay(Timeout)) == task) return await task;
            _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            throw new TimeoutException($"{what} took longer than {Timeout.TotalSeconds:0.#} s");
        }

        /// <summary>Forgets what is queued in memory, for tests (the journal in Redis stays).</summary>
        internal void ResetForTests()
        {
            lock (_gate) _queued = 0;
        }
    }
}
