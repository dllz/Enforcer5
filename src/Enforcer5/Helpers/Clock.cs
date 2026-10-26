using System;
using System.Threading;
using System.Threading.Tasks;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// The time source for rate limiting, the enforcement queue, the command limiter and raid
    /// handling. It is the system clock in the bot. The test harness swaps in a simulated clock, so a
    /// 30-minute raid runs in milliseconds with no sleeps, which keeps the tests fast enough for the
    /// Docker build.
    /// <para>
    /// Older code still reads <see cref="DateTime.UtcNow"/> directly. Move it here when you touch it.
    /// </para>
    /// </summary>
    internal static class Clock
    {
        private static TimeProvider _provider = TimeProvider.System;

        // Dispatched work that is waiting on the clock rather than doing anything. The harness counts
        // dispatched work as idle once all of it is parked here, and moves the simulated clock on.
        private static int _parked;

        internal static TimeProvider Provider => _provider;

        internal static DateTime UtcNow => _provider.GetUtcNow().UtcDateTime;

        /// <summary>Dispatched work currently waiting on <see cref="Delay"/> or <see cref="WaitAsync"/>.</summary>
        internal static int Parked => Volatile.Read(ref _parked);

        /// <summary>Points the clock at a stand-in, for the test harness. Null puts the system clock back.</summary>
        internal static void UseForTests(TimeProvider provider) => _provider = provider ?? TimeProvider.System;

        /// <summary>
        /// Counts the current dispatched work as parked until <see cref="Unpark"/>: for a wait that
        /// only ends when parked work moves on, such as a permit held by it.
        /// </summary>
        internal static void Park() => Interlocked.Increment(ref _parked);

        /// <summary>Ends <see cref="Park"/>.</summary>
        internal static void Unpark() => Interlocked.Decrement(ref _parked);

        /// <summary>Waits <paramref name="delay"/> on this clock.</summary>
        internal static Task Delay(TimeSpan delay) => WaitAsync(delay, CancellationToken.None);

        /// <summary>
        /// Waits <paramref name="delay"/> on this clock, or until <paramref name="wake"/> is cancelled.
        /// Returns true when woken early. Never throws for the wake.
        /// </summary>
        internal static Task<bool> WaitAsync(TimeSpan delay, CancellationToken wake)
        {
            if (wake.IsCancellationRequested) return Task.FromResult(true);
            if (delay <= TimeSpan.Zero) return Task.FromResult(false);

            var unit = Bot.CurrentWork;
            var tracked = unit != null;
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var finished = 0;
            ITimer timer = null;
            var registration = default(CancellationTokenRegistration);

            // The parked count drops inside the timer callback itself, not in a continuation. The
            // harness fires timers synchronously, so by the time its clock has moved on, it can
            // already see this work as running again and wait for it.
            void Finish(bool woken)
            {
                if (Interlocked.Exchange(ref finished, 1) != 0) return;
                if (tracked)
                {
                    Interlocked.Decrement(ref _parked);
                    unit.WaitEnded();
                }
                timer?.Dispose();
                registration.Dispose();
                done.TrySetResult(woken);
            }

            if (tracked)
            {
                unit.WaitStarted();
                Interlocked.Increment(ref _parked);
            }
            timer = _provider.CreateTimer(_ => Finish(false), null, delay, Timeout.InfiniteTimeSpan);
            if (Volatile.Read(ref finished) != 0) timer.Dispose();
            if (wake.CanBeCanceled) registration = wake.Register(() => Finish(true));
            return done.Task;
        }

        /// <summary>
        /// Waits for <paramref name="task"/> at most <paramref name="timeout"/> on this clock.
        /// True when the task finished in time. Blocks the calling thread, for the synchronous callers.
        /// </summary>
        internal static bool Wait(Task task, TimeSpan timeout)
        {
            if (task.IsCompleted) return true;
            using var cancel = new CancellationTokenSource();
            var timer = WaitAsync(timeout, cancel.Token);
            Task.WhenAny(task, timer).GetAwaiter().GetResult();
            cancel.Cancel();
            return task.IsCompleted;
        }
    }
}
