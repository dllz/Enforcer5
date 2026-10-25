using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Enforcer5.Storage;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// The one background worker that writes moderation history (bans, warns) to PostgreSQL,
    /// without ever holding up the action itself.
    /// <para>
    /// Callers report what Telegram and Redis have already done; the writes happen in the order the
    /// calls were made, across domains, so a ban and the unban right after it cannot land the wrong
    /// way round, and a warn is written before the max-warns ban it caused. A failed write is
    /// retried twice and then reported; the action still stands. While PostgreSQL is not configured
    /// or not ready, writes are dropped: the history is additive and Redis is still what the bot
    /// runs on.
    /// </para>
    /// </summary>
    internal static class HistoryWriter
    {
        private const int QueueDepth = 10_000;

        private static readonly Channel<Func<Task>> _queue =
            Channel.CreateBounded<Func<Task>>(new BoundedChannelOptions(QueueDepth)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true
            });

        private static int _started;

        /// <summary>True when a write would be made. Settable so tests can turn it on.</summary>
        internal static Func<bool> Enabled { get; set; } = () => PostgresStorage.Ready;

        /// <summary>Delay between attempts. Settable so tests do not sleep.</summary>
        internal static TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(2);

        internal const int Attempts = 3;

        /// <summary>
        /// Queues <paramref name="write"/>. <paramref name="what"/> says what it records, for the
        /// error report if it fails. Never throws.
        /// </summary>
        internal static void Enqueue(Func<Task> write, string what)
        {
            try
            {
                if (!Enabled()) return;
                EnsureWorker();
                if (!_queue.Writer.TryWrite(() => RunWithRetry(write, what)))
                    ErrorReporter.Report($"History queue full ({QueueDepth}); dropped: {what}");
            }
            catch (Exception e)
            {
                // Never let history get in the way of the action that was just taken.
                ErrorReporter.Report(e, what);
            }
        }

        private static async Task RunWithRetry(Func<Task> write, string what)
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    await write();
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
            // Its own long-running task, not Bot.Dispatch: it never finishes, and Dispatch counts
            // work in flight (Bot.WaitIdleAsync). FlushAsync is how callers wait for it.
            Task.Factory.StartNew(async () =>
            {
                await foreach (var write in _queue.Reader.ReadAllAsync())
                {
                    try
                    {
                        await write();
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Report(e, "History worker");
                    }
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap()
                .ContinueWith(t => ErrorReporter.Report(t.Exception, "History worker stopped"),
                    TaskContinuationOptions.OnlyOnFaulted);
        }

        /// <summary>Waits until everything queued so far is written. For tests.</summary>
        internal static async Task FlushAsync()
        {
            var done = new TaskCompletionSource();
            if (!_queue.Writer.TryWrite(() => { done.SetResult(); return Task.CompletedTask; }))
                throw new InvalidOperationException("History queue full");
            EnsureWorker();
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }

        /// <summary>Reasons can be a whole replied-to message; the history needs the gist.</summary>
        internal static string Trim(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return null;
            reason = reason.Trim();
            return reason.Length <= 500 ? reason : reason.Substring(0, 499) + "…";
        }
    }
}
