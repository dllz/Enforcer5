using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Storage;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Records bans and unbans in the ban history without ever holding up the ban itself.
    /// <para>
    /// Callers report what Telegram has already done; the write happens on one background worker,
    /// in the order the calls were made, so a ban and the unban right after it cannot land the
    /// wrong way round. A failed write is retried twice and then reported; the ban still stands.
    /// While PostgreSQL is not configured or not ready, events are dropped: the history is
    /// additive and Redis is still what enforcement runs on.
    /// </para>
    /// </summary>
    internal static class BanHistory
    {
        private const int QueueDepth = 10_000;

#if PREMIUM
        internal const string Edition = "premium";
#else
        internal const string Edition = "normal";
#endif

        private static readonly Channel<Func<IBanHistoryRepository, Task>> _queue =
            Channel.CreateBounded<Func<IBanHistoryRepository, Task>>(new BoundedChannelOptions(QueueDepth)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true
            });

        private static int _started;

        /// <summary>True when an event would be recorded. Settable so tests can turn it on.</summary>
        internal static Func<bool> Enabled { get; set; } = () => PostgresStorage.Ready;

        /// <summary>Delay between attempts. Settable so tests do not sleep.</summary>
        internal static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

        internal const int Attempts = 3;

        /// <summary>A ban Telegram has accepted.</summary>
        internal static void Banned(long chatId, long userId, BanKind kind, string reason, long? actorId,
            DateTime? expiresUtc = null)
        {
            var ban = new BanEvent(chatId, userId, kind, Trim(reason), actorId, DateTimeOffset.UtcNow,
                expiresUtc.HasValue ? new DateTimeOffset(DateTime.SpecifyKind(expiresUtc.Value, DateTimeKind.Utc)) : null,
                Edition);
            Enqueue(repo => repo.RecordBanAsync(ban), $"Recording the ban of {userId} in {chatId}");
        }

        /// <summary>An unban Telegram has accepted.</summary>
        internal static void Unbanned(long chatId, long userId, BanEnd end, long? actorId)
        {
            var unban = new UnbanEvent(chatId, userId, end, actorId, DateTimeOffset.UtcNow);
            Enqueue(repo => repo.RecordUnbanAsync(unban), $"Recording the unban of {userId} in {chatId}");
        }

        private static void Enqueue(Func<IBanHistoryRepository, Task> write, string what)
        {
            try
            {
                if (!Enabled()) return;
                EnsureWorker();
                if (!_queue.Writer.TryWrite(async repo => await RunWithRetry(repo, write, what)))
                    ErrorReporter.Report($"Ban history queue full ({QueueDepth}); dropped: {what}");
            }
            catch (Exception e)
            {
                // Never let history get in the way of the ban that was just made.
                ErrorReporter.Report(e, what);
            }
        }

        private static async Task RunWithRetry(IBanHistoryRepository repo, Func<IBanHistoryRepository, Task> write,
            string what)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await write(repo);
                    return;
                }
                catch (Exception e) when (attempt < Attempts)
                {
                    LogHelper.Error($"{what} failed (attempt {attempt}), retrying: {e.Message}");
                    await Task.Delay(RetryDelay);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"{what}, after {Attempts} attempts; it is missing from the history");
                    return;
                }
            }
        }

        private static void EnsureWorker()
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            Bot.Dispatch(async () =>
            {
                await foreach (var write in _queue.Reader.ReadAllAsync())
                {
                    try
                    {
                        await write(Repositories.BanHistory);
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Report(e, "Ban history worker");
                    }
                }
            }, "BanHistory");
        }

        /// <summary>Waits until everything queued so far is written. For tests.</summary>
        internal static async Task FlushAsync()
        {
            var done = new TaskCompletionSource();
            if (!_queue.Writer.TryWrite(_ => { done.SetResult(); return Task.CompletedTask; }))
                throw new InvalidOperationException("Ban history queue full");
            EnsureWorker();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        // Reasons can be a whole replied-to message; the history needs the gist.
        private static string Trim(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return null;
            reason = reason.Trim();
            return reason.Length <= 500 ? reason : reason.Substring(0, 499) + "…";
        }
    }
}
