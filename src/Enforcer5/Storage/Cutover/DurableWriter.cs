using System;
using System.Threading;
using System.Threading.Tasks;

namespace Enforcer5.Storage.Cutover
{
    /// <summary>
    /// A registered durable writer. Each unit of work that mutates Redis or PostgreSQL is wrapped
    /// in <see cref="BeginWork"/>, which both refuses to start while the barrier is raised and
    /// lets the barrier see work already in progress.
    /// <para>
    /// Granularity matters: wrap a whole logical mutation, not an individual command. The barrier
    /// guarantees no writer is part-way through a unit, so a unit that spans several Redis writes
    /// must be one scope.
    /// </para>
    /// </summary>
    internal sealed class DurableWriter
    {
        private readonly object _sync = new object();
        private readonly ManualResetEventSlim _idle = new ManualResetEventSlim(true);
        private int _inFlight;

        internal DurableWriter(string name, WriterResumeClass resumeClass)
        {
            Name = name;
            ResumeClass = resumeClass;
        }

        internal string Name { get; }
        internal WriterResumeClass ResumeClass { get; }

        internal int InFlight
        {
            get { lock (_sync) return _inFlight; }
        }

        /// <summary>
        /// Waits for the barrier to clear, then marks a unit of durable work as in flight.
        /// Dispose the returned scope when the unit completes.
        /// </summary>
        internal async Task<IDisposable> BeginWorkAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                await QuiesceBarrier.WaitWhileQuiescedAsync(cancellationToken);
                lock (_sync)
                {
                    // Re-check under the lock. Without this a quiesce raised between the wait
                    // returning and the increment would see zero in flight and declare quiet
                    // while this unit was starting.
                    if (QuiesceBarrier.IsQuiescing) continue;
                    _inFlight++;
                    _idle.Reset();
                    return new WorkScope(this);
                }
            }
        }

        /// <summary>
        /// Blocking form, for the many callers in this codebase that are synchronous.
        /// </summary>
        internal IDisposable BeginWork(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                QuiesceBarrier.WaitWhileQuiesced(cancellationToken);
                lock (_sync)
                {
                    if (QuiesceBarrier.IsQuiescing) continue;
                    _inFlight++;
                    _idle.Reset();
                    return new WorkScope(this);
                }
            }
        }

        /// <summary>
        /// Safe point for loop-driven writers that hold no work scope: returns only once the
        /// barrier is clear. Use at the top of a polling loop.
        /// </summary>
        internal void Gate(CancellationToken cancellationToken = default) =>
            QuiesceBarrier.WaitWhileQuiesced(cancellationToken);

        internal Task GateAsync(CancellationToken cancellationToken = default) =>
            QuiesceBarrier.WaitWhileQuiescedAsync(cancellationToken);

        internal async Task<bool> WaitUntilIdleAsync(TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (_idle.IsSet) return true;
            try
            {
                return await Task.Run(() => _idle.Wait(timeout, cancellationToken), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        private void EndWork()
        {
            lock (_sync)
            {
                if (_inFlight == 0) return;
                if (--_inFlight == 0) _idle.Set();
            }
        }

        private sealed class WorkScope : IDisposable
        {
            private readonly DurableWriter _writer;
            private int _disposed;

            internal WorkScope(DurableWriter writer) => _writer = writer;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                _writer.EndWork();
            }
        }
    }
}
