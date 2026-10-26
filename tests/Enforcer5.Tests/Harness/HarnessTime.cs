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
        /// already due, PostgreSQL writes included. Time does not move.
        /// </summary>
        internal static async Task SettleAsync(FakeClock clock)
        {
            while (true)
            {
                await Bot.WaitIdleAsync(TimeSpan.FromSeconds(30));
                await HistoryWriter.FlushAsync();
                await PrincipalObserver.FlushAsync();
                var next = clock.NextDue;
                if (next == null || next > clock.GetUtcNow())
                {
                    // Flushing may have dispatched more; once more to be sure.
                    await Bot.WaitIdleAsync(TimeSpan.FromSeconds(30));
                    if (clock.NextDue is { } again && again <= clock.GetUtcNow()) continue;
                    return;
                }
                clock.AdvanceTo(clock.GetUtcNow());
            }
        }

        /// <summary>
        /// Lets <paramref name="by"/> of simulated time pass: each timer fires at its due time, and
        /// what it sets off runs to completion (or to its next wait) before time moves on.
        /// </summary>
        internal static async Task AdvanceAsync(FakeClock clock, TimeSpan by)
        {
            var target = clock.GetUtcNow() + by;
            await SettleAsync(clock);
            while (clock.NextDue is { } next && next <= target)
            {
                clock.AdvanceTo(next);
                await SettleAsync(clock);
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
            await SettleAsync(clock);
            while (Bot.PendingWork > 0 && clock.NextDue is { } next && next <= until)
            {
                clock.AdvanceTo(next);
                await SettleAsync(clock);
            }
        }
    }
}
