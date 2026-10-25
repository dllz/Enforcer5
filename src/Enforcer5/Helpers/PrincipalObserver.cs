using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Storage;
using StackExchange.Redis;
using Telegram.Bot.Types;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Keeps PostgreSQL's accounts, chats and groups current from the traffic the bot sees: names,
    /// usernames, languages, group titles and links, when each was last seen, and how many users
    /// a group has been seen with.
    /// <para>
    /// The update path only records what it saw in memory (<see cref="Observe"/>, a dictionary
    /// write under a lock). Once a minute a background worker writes everything collected in one
    /// batch. A batch that fails goes back into the buffer for the next minute and is reported. Off
    /// while PostgreSQL is not ready.
    /// </para>
    /// </summary>
    internal static class PrincipalObserver
    {
        private static readonly ObservationBuffer Buffer = new ObservationBuffer();
        private static readonly SemaphoreSlim FlushGate = new SemaphoreSlim(1, 1);
        private static int _started;

        /// <summary>True when observations are collected. Settable so tests can turn it on.</summary>
        internal static Func<bool> Enabled { get; set; } = () => PostgresStorage.Ready;

        internal static TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>Records what an update says about its accounts and chats. Never throws.</summary>
        internal static void Observe(Update update)
        {
            try
            {
                if (!Enabled()) return;
                Buffer.Collect(update);
                EnsureWorker();
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Observing an update for PostgreSQL");
            }
        }

        private static void EnsureWorker()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            // Its own long-running loop, not Bot.Dispatch, which counts work in flight.
            Task.Factory.StartNew(async () =>
            {
                while (!Bot.ShutdownToken.IsCancellationRequested)
                {
                    try { await Task.Delay(Interval, Bot.ShutdownToken); }
                    catch (OperationCanceledException) { return; } // shutting down; StopReceiving flushes
                    await FlushAsync();
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
        }

        /// <summary>Writes everything observed so far. Never throws; failures are reported.</summary>
        internal static async Task FlushAsync()
        {
            await FlushGate.WaitAsync();
            ObservationBatch batch = null;
            try
            {
                var (taken, dropped) = Buffer.Take();
                batch = taken;
                if (dropped > 0)
                    ErrorReporter.Report($"PostgreSQL observation buffer full; {dropped} observations dropped");
                if (batch.IsEmpty || !PostgresStorage.Ready) return;
                batch = await WithRedisDetailsAsync(batch);
                await Repositories.Principals.ApplyAsync(batch, DateTimeOffset.UtcNow);
                batch = null;
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Writing {batch?.Accounts.Count} accounts and {batch?.Chats.Count} chats to PostgreSQL; retrying next minute");
                if (batch != null) Buffer.Restore(batch);
            }
            finally
            {
                FlushGate.Release();
            }
        }

        /// <summary>
        /// Adds what only Redis knows about each group: how many users it has been seen with
        /// (<c>chat:{id}:userlast</c>) and the invite link set with /setlink (<c>chat:{id}links</c>).
        /// One pipelined round trip; a group whose reads fail keeps its stored values.
        /// </summary>
        private static async Task<ObservationBatch> WithRedisDetailsAsync(ObservationBatch batch)
        {
            var groups = batch.Chats.Where(c => c.IsGroup).ToList();
            if (groups.Count == 0) return batch;
            var db = Redis.db;
            var reads = groups.ToDictionary(g => g.TelegramChatId, g => (
                Count: db.HashLengthAsync($"chat:{g.TelegramChatId}:userlast"),
                Link: db.HashGetAsync($"chat:{g.TelegramChatId}links", "link")));
            try
            {
                await Task.WhenAll(reads.Values.SelectMany(r => new Task[] { r.Count, r.Link }));
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Reading group details from Redis for PostgreSQL");
            }

            return batch with
            {
                Chats = batch.Chats.Select(c =>
                {
                    if (!c.IsGroup) return c;
                    var r = reads[c.TelegramChatId];
                    var count = r.Count.Status == TaskStatus.RanToCompletion ? r.Count.Result : (long?)null;
                    var linkKnown = r.Link.Status == TaskStatus.RanToCompletion;
                    return c with
                    {
                        ObservedUserCount = count,
                        LinkKnown = linkKnown,
                        PublicLink = linkKnown ? PublicLink(c.Username, r.Link.Result) : null
                    };
                }).ToList()
            };
        }

        /// <summary>A public group's t.me link, else the invite link an admin set ("no" means removed).</summary>
        internal static string PublicLink(string username, RedisValue stored)
        {
            if (!string.IsNullOrEmpty(username)) return $"https://t.me/{username}";
            if (stored.IsNullOrEmpty || stored == "no") return null;
            return stored.ToString();
        }
    }
}
