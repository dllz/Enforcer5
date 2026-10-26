using System;
using System.Threading.Tasks;
using Enforcer5.Helpers;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Moves a <see cref="FakeClock"/> on the way the bot would experience time: only once all
    /// dispatched work is done or waiting on the clock (<see cref="Bot.WaitIdleAsync"/>), one due
    /// timer at a time.
    /// </summary>
    internal static class HarnessTime
    {
        /// <summary>
        /// Waits until everything dispatched is done or waiting on the clock, firing timers that are
        /// already due. Time does not move. <paramref name="flush"/> also waits for the PostgreSQL
        /// writes, which costs a database round trip: leave it off between the messages of a burst.
        /// </summary>
        internal static async Task SettleAsync(FakeClock clock, bool flush = true)
        {
            await RunDueAsync(clock);
            if (flush) await FlushAsync(clock);
        }

        // Fires what is due now and waits until all dispatched work is idle again; no flushing,
        // which costs a database round trip and is only needed before a scenario looks.
        private static async Task RunDueAsync(FakeClock clock)
        {
            while (true)
            {
                await Bot.WaitIdleAsync(TimeSpan.FromSeconds(30));
                if (clock.NextDue is not { } next || next > clock.GetUtcNow()) return;
                clock.AdvanceTo(clock.GetUtcNow());
            }
        }

        // PostgreSQL writes, which may set off more work, until nothing is left.
        private static async Task FlushAsync(FakeClock clock)
        {
            await HistoryWriter.FlushAsync();
            await PrincipalObserver.FlushAsync();
            await RunDueAsync(clock);
        }

        /// <summary>
        /// Lets <paramref name="by"/> of simulated time pass: each timer fires at its due time, and
        /// what it sets off runs to completion (or to its next wait) before time moves on.
        /// </summary>
        internal static async Task AdvanceAsync(FakeClock clock, TimeSpan by)
        {
            var target = clock.GetUtcNow() + by;
            await RunDueAsync(clock);
            while (clock.NextDue is { } next && next <= target)
            {
                clock.AdvanceTo(next);
                await RunDueAsync(clock);
            }
            clock.AdvanceTo(target);
            await SettleAsync(clock);
        }

        /// <summary>
        /// Lets simulated time run until everything dispatched has finished, background loops left
        /// out. At most <paramref name="limit"/> (10 minutes).
        /// </summary>
        internal static async Task CompleteAsync(FakeClock clock, TimeSpan? limit = null)
        {
            var until = clock.GetUtcNow() + (limit ?? TimeSpan.FromMinutes(10));
            await RunDueAsync(clock);
            while (Bot.PendingWork > 0 && clock.NextDue is { } next && next <= until)
            {
                clock.AdvanceTo(next);
                await RunDueAsync(clock);
            }
            await SettleAsync(clock);
        }
    }
}
