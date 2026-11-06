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
    /// The edition's tempban schedule (rollout step 13): PostgreSQL (<see cref="PostgresTempbanRepository"/>), with
    /// changes queued in Redis while it is down, as the warn counts are (Daniel, 2026-11-06).
    /// <list type="bullet">
    /// <item>At startup, once PostgreSQL is ready, the old Redis schedule is imported once (<see cref="LegacyTempbans"/>).
    /// Until then changes queue, so a ban or rejoin cannot be undone by the import adding an older entry back.</item>
    /// <item>While PostgreSQL is down, a schedule or cancel goes to <c>tempbans:queue:{edition}</c> and is written once
    /// it is back, in order; both are idempotent, so writing one twice is harmless. While anything is queued, new
    /// changes queue behind it and the expiry check waits. Telegram lifts a tempban by itself at its until date
    /// (when under 366 days), so an outage only delays the bot's own unban.</item>
    /// <item>The members scheduled are also kept in memory, so a join (every join cancels the joiner's tempban) costs
    /// nothing for the many who have none.</item>
    /// </list>
    /// </summary>
    internal sealed class TempbanStore : ITempbanRepository
    {
        internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(3);

        internal static readonly TimeSpan DrainEvery = TimeSpan.FromSeconds(5);

        private readonly PostgresTempbanRepository _store;
        private readonly LegacyTempbans _legacy;
        private readonly Func<IDatabaseAsync> _redis;
        private readonly Func<bool> _ready;
        private readonly object _gate = new();
        private readonly SemaphoreSlim _draining = new(1, 1);
        private readonly ConcurrentDictionary<(long, long), byte> _known = new();
        private volatile bool _imported;
        private volatile bool _knownLoaded;
        private int _queued;
        private long _sequence;

        internal TempbanStore(PostgresTempbanRepository store, LegacyTempbans legacy, Func<IDatabaseAsync> redis, Func<bool> ready)
        {
            _store = store;
            _legacy = legacy;
            _redis = redis;
            _ready = ready;
            QueueKey = $"tempbans:queue:{store.Edition}";
        }

        internal string QueueKey { get; }

        internal int Queued => Volatile.Read(ref _queued);

        /// <summary>True once the old schedule was imported (or had been before) and the known members loaded.</summary>
        internal bool Started => _imported && _knownLoaded;

        private bool Direct
        {
            get { lock (_gate) return _queued == 0 && _imported && _ready(); }
        }

        /// <summary>A change waiting in the queue.</summary>
        internal sealed record Change(string Id, long Sequence, string Kind, long ChatId, long UserId, long Until, long Now);

        // ------------------------------------------------------------------ changes

        public async Task<long> ScheduleAsync(long chatId, long userId, DateTime untilUtc, DateTime nowUtc)
        {
            _known[(chatId, userId)] = 0;
            var until = PostgresTempbanRepository.Second(untilUtc).ToUnixTimeSeconds();
            await ChangeAsync(new Change(null, 0, "schedule", chatId, userId, until,
                PostgresTempbanRepository.Second(nowUtc).ToUnixTimeSeconds()));
            return until;
        }

        public async Task CancelAsync(long chatId, long userId)
        {
            if (_knownLoaded && !_known.ContainsKey((chatId, userId))) return;
            _known.TryRemove((chatId, userId), out _);
            await ChangeAsync(new Change(null, 0, "cancel", chatId, userId, 0, 0));
        }

        private async Task ChangeAsync(Change change)
        {
            change = change with { Id = "tempban:" + Guid.NewGuid().ToString("N"), Sequence = NextSequence() };
            bool direct;
            lock (_gate)
            {
                direct = _queued == 0 && _imported && _ready();
                if (!direct) _queued++;
            }
            if (direct)
            {
                try
                {
                    await WithTimeout(ApplyAsync(change), $"Changing the tempban of {change.UserId} in {change.ChatId}");
                    return;
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Changing the tempban of {change.UserId} in {change.ChatId} in PostgreSQL; queued in Redis instead");
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
            if (change.Kind == "schedule")
                await _store.ScheduleAsync(change.ChatId, change.UserId, DateTimeOffset.FromUnixTimeSeconds(change.Until).UtcDateTime,
                    DateTimeOffset.FromUnixTimeSeconds(change.Now).UtcDateTime);
            else
                await _store.CancelAsync(change.ChatId, change.UserId);
            return true;
        }

        // ------------------------------------------------------------------ reads and the expiry check

        public async Task<IReadOnlyList<TimedRestriction>> GetActiveAsync(long chatId, DateTime nowUtc)
        {
            await DrainAsync();
            if (!_ready()) throw new InvalidOperationException("The tempban list is in PostgreSQL, which is not reachable now.");
            return await _store.GetActiveAsync(chatId, nowUtc);
        }

        public async Task<IReadOnlyList<UserTempban>> OfUserAsync(long userId, DateTime nowUtc)
        {
            await DrainAsync();
            if (!_ready()) throw new InvalidOperationException("The tempban list is in PostgreSQL, which is not reachable now.");
            return await _store.OfUserAsync(userId, nowUtc);
        }

        /// <summary>The due tempbans; none while changes wait in the queue (they could cancel one).</summary>
        public async Task<IReadOnlyList<TempbanEntry>> DueAsync(DateTime nowUtc)
        {
            await DrainAsync();
            return Direct ? await _store.DueAsync(nowUtc) : Array.Empty<TempbanEntry>();
        }

        public Task RemoveAsync(TempbanEntry entry) => _store.RemoveAsync(entry);

        // ------------------------------------------------------------------ startup and the queue

        /// <summary>
        /// Imports the old schedule (once ever), loads the members scheduled and how many changes wait. Throws when
        /// PostgreSQL fails; <see cref="Start"/> tries again.
        /// </summary>
        internal async Task StartUpAsync(DateTime nowUtc)
        {
            var waiting = (await ReadQueueAsync()).Count;
            lock (_gate) _queued = waiting;
            if (!_imported)
            {
                var entries = await _legacy.ReadAsync();
                var imported = await _store.ImportAsync(entries, nowUtc);
                if (imported != null)
                    LogHelper.Info($"Tempbans: imported {imported} from the Redis schedule {_legacy.Index} ({entries.Count} entries)");
                _imported = true;
            }
            foreach (var key in await _store.ScheduledAsync()) _known[key] = 0;
            _knownLoaded = true;
            if (waiting > 0) LogHelper.Info($"Tempbans: {waiting} changes queued from before the restart");
        }

        /// <summary>Writes the queued changes in order once PostgreSQL is there; stops at the first failure. Never throws.</summary>
        internal async Task DrainAsync()
        {
            if (Queued == 0 || !_imported || !_ready()) return;
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
                ErrorReporter.Report(e, $"Writing queued tempban changes to PostgreSQL ({Queued} waiting); trying again shortly");
            }
            finally
            {
                _draining.Release();
            }
        }

        /// <summary>Starts up once PostgreSQL is ready and keeps draining the queue, in the background. Never throws.</summary>
        internal void Start(CancellationToken stop) =>
            Bot.StartService(async ct =>
            {
                var reported = false;
                while (!ct.IsCancellationRequested && !Started)
                {
                    if (_ready())
                    {
                        try
                        {
                            await StartUpAsync(Clock.UtcNow);
                            break;
                        }
                        catch (Exception e)
                        {
                            if (!reported) ErrorReporter.Report(e, "Starting the tempban schedule; tempbans queue until it works");
                            reported = true;
                        }
                    }
                    await Clock.WaitAsync(TimeSpan.FromSeconds(10), ct);
                }
                while (!ct.IsCancellationRequested)
                {
                    await DrainAsync();
                    await Clock.WaitAsync(DrainEvery, ct);
                }
            }, stop, "TempbanSchedule");

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
                    ErrorReporter.Report(e, $"Reading queued tempban change {entry.Name}, dropped");
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

        /// <summary>Forgets the state in memory, for tests (the journal in Redis and PostgreSQL stay).</summary>
        internal void ResetForTests(bool started = false)
        {
            lock (_gate) _queued = 0;
            _known.Clear();
            _imported = started;
            _knownLoaded = started;
        }
    }
}
