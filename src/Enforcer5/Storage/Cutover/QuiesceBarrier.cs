using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Helpers;

namespace Enforcer5.Storage.Cutover
{
    /// <summary>
    /// How a writer recovers the work it did not do while quiesced.
    /// </summary>
    internal enum WriterResumeClass
    {
        /// <summary>
        /// Re-evaluates its whole workload from durable state on the next tick, so stopping it
        /// loses nothing. <c>Methods.CheckTempBans</c> is the reference case: it reads the entire
        /// tempban hash every 30s and processes whatever is due, so anything that came due during
        /// the pause is still due afterwards.
        /// </summary>
        Time,

        /// <summary>
        /// Resumes from a durable position - a Redis stream consumer group, or the Telegram update
        /// offset in <c>bot:last_update</c>. Safe to stop provided the position is not advanced
        /// past unprocessed work.
        /// </summary>
        Cursor,

        /// <summary>
        /// Derives its workload from in-memory state that is itself rebuilt from incoming updates.
        /// Once updates are paused it has nothing new to act on. Any Redis writes it makes are
        /// ephemeral rather than canonical.
        /// </summary>
        Ephemeral
    }

    internal sealed record QuiesceWriterReport(string Name, WriterResumeClass ResumeClass,
        bool Quiet, TimeSpan TimeToQuiet, int InFlightAtTimeout);

    internal sealed record QuiesceReport(bool Quiet, TimeSpan Elapsed,
        IReadOnlyList<QuiesceWriterReport> Writers)
    {
        internal IEnumerable<QuiesceWriterReport> Stragglers => Writers.Where(w => !w.Quiet);
    }

    /// <summary>
    /// Stops every durable writer at a safe point and waits until all of them are idle.
    /// <para>
    /// The migration needs a moment when nothing is mutating Redis. Pausing the Telegram poll loop
    /// alone is not enough: the tempban worker unbans on a timer and does so with a hard
    /// <c>HashDelete</c>, which is exactly the case a snapshot loader cannot tolerate - it can
    /// re-insert a sanction that was removed while the load was running.
    /// </para>
    /// <para>
    /// Pausing is lossless rather than destructive. The poll loop blocks on a full channel,
    /// <c>bot:last_update</c> stops advancing, and Telegram redelivers on resume within its 24h
    /// retention window.
    /// </para>
    /// </summary>
    internal static class QuiesceBarrier
    {
        private static readonly object Sync = new object();
        private static readonly List<DurableWriter> Writers = new List<DurableWriter>();

        private static bool _quiescing;
        private static TaskCompletionSource _resume = CompletedSource();

        /// <summary>True while a quiesce is in effect. Writers must not begin new durable work.</summary>
        internal static bool IsQuiescing
        {
            get { lock (Sync) return _quiescing; }
        }

        /// <summary>
        /// Registers a writer. Registration is idempotent by name so that a writer restarted during
        /// the process lifetime does not accumulate duplicate entries.
        /// </summary>
        internal static DurableWriter Register(string name, WriterResumeClass resumeClass)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Writer name is required.", nameof(name));

            lock (Sync)
            {
                var existing = Writers.FirstOrDefault(w =>
                    string.Equals(w.Name, name, StringComparison.Ordinal));
                if (existing != null) return existing;

                var writer = new DurableWriter(name, resumeClass);
                Writers.Add(writer);
                return writer;
            }
        }

        internal static IReadOnlyList<DurableWriter> Registered
        {
            get { lock (Sync) return Writers.ToArray(); }
        }

        /// <summary>
        /// Blocks new durable work and waits for work already in flight to finish.
        /// <para>
        /// Always call <see cref="Resume"/> afterwards, including when this reports not quiet -
        /// leaving the barrier raised silently stops the bot.
        /// </para>
        /// </summary>
        internal static async Task<QuiesceReport> QuiesceAsync(TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            DurableWriter[] writers;
            lock (Sync)
            {
                if (_quiescing)
                    throw new InvalidOperationException("A quiesce is already in effect.");
                _quiescing = true;
                _resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                writers = Writers.ToArray();
            }

            LogHelper.Info($"Quiesce requested across {writers.Length} durable writer(s).");
            var overall = Stopwatch.StartNew();
            var reports = new List<QuiesceWriterReport>(writers.Length);

            foreach (var writer in writers)
            {
                var remaining = timeout - overall.Elapsed;
                if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;

                var writerClock = Stopwatch.StartNew();
                var quiet = await writer.WaitUntilIdleAsync(remaining, cancellationToken);
                writerClock.Stop();

                reports.Add(new QuiesceWriterReport(writer.Name, writer.ResumeClass, quiet,
                    writerClock.Elapsed, writer.InFlight));

                if (!quiet)
                    LogHelper.Error($"Quiesce: '{writer.Name}' still has {writer.InFlight} " +
                                    "unit(s) of work in flight after the timeout.");
            }

            overall.Stop();
            var allQuiet = reports.All(r => r.Quiet);
            LogHelper.Info($"Quiesce {(allQuiet ? "reached" : "INCOMPLETE")} in " +
                           $"{overall.Elapsed.TotalSeconds:N2}s.");
            return new QuiesceReport(allQuiet, overall.Elapsed, reports);
        }

        /// <summary>Releases the barrier. Safe to call when no quiesce is in effect.</summary>
        internal static void Resume()
        {
            TaskCompletionSource resume;
            lock (Sync)
            {
                if (!_quiescing) return;
                _quiescing = false;
                resume = _resume;
                _resume = CompletedSource();
            }
            resume.TrySetResult();
            LogHelper.Info("Quiesce released; durable writers resumed.");
        }

        /// <summary>
        /// Returns once the barrier is not raised. Called by writers before beginning durable work.
        /// </summary>
        internal static Task WaitWhileQuiescedAsync(CancellationToken cancellationToken)
        {
            lock (Sync)
            {
                if (!_quiescing) return Task.CompletedTask;
                return _resume.Task.WaitAsync(cancellationToken);
            }
        }

        internal static void WaitWhileQuiesced(CancellationToken cancellationToken)
        {
            while (true)
            {
                Task resume;
                lock (Sync)
                {
                    if (!_quiescing) return;
                    resume = _resume.Task;
                }
                resume.WaitAsync(cancellationToken).GetAwaiter().GetResult();
            }
        }

        /// <summary>Test and diagnostic hook; drops all registrations.</summary>
        internal static void ResetForTests()
        {
            lock (Sync)
            {
                _quiescing = false;
                _resume = CompletedSource();
                Writers.Clear();
            }
        }

        private static TaskCompletionSource CompletedSource()
        {
            var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }
    }
}
