using System;
using System.Threading.Tasks;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// The pace of the lookups made in the background for detection (where a mention points, members' profiles,
    /// channels): at most <see cref="PerSecond"/> a second for all of them together, bot-wide, and a pause for
    /// everyone after a 429. Shared so that the lookup queues never add up to more than this; moderation and
    /// messages have their own lanes (<see cref="FloodControl"/>) and never wait behind it.
    /// </summary>
    internal static class LookupPacer
    {
        internal const int PerSecond = 10;

        private static readonly object Lock = new();
        private static DateTime _next;

        /// <summary>Waits for the next slot and takes it.</summary>
        internal static async Task TurnAsync()
        {
            while (true)
            {
                DateTime wait;
                var now = Clock.UtcNow;
                lock (Lock)
                {
                    if (_next <= now)
                    {
                        // A little over the even spacing, so no second ever holds more than PerSecond.
                        _next = now + TimeSpan.FromSeconds(1.05 / PerSecond);
                        return;
                    }
                    wait = _next;
                }
                await Clock.Delay(wait - now);
            }
        }

        /// <summary>Telegram answered 429: nobody looks anything up until <paramref name="retryAfter"/> has passed.</summary>
        internal static void Pause(TimeSpan retryAfter)
        {
            var until = Clock.UtcNow + retryAfter;
            lock (Lock)
                if (_next < until) _next = until;
        }

        internal static void ResetForTests()
        {
            lock (Lock) _next = DateTime.MinValue;
        }
    }
}
