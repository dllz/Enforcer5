using System;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Where a 429 one bot process sees could be shared with the others. Telegram's limits count per
    /// bot token, so the normal and premium editions, or another process on the same token, each
    /// learn about a pause only by being refused themselves; and when one edition is rate limited in
    /// a group, the other (if it is an admin there too) could carry out the enforcement instead.
    /// <para>
    /// Not built yet: <see cref="FloodSignal.Current"/> is a no-op. The PostgreSQL branch will put a
    /// Redis-backed implementation here (pub/sub for the pauses, a shared stream for enforcement
    /// jobs; see docs/postgres-rollout.md). Nothing else needs to change for it: the gateway already
    /// publishes every 429 through <see cref="FloodSignal.Publish"/>, and a subscriber hands pauses
    /// heard from elsewhere to <see cref="FloodControl.ImposePause"/>.
    /// </para>
    /// </summary>
    internal interface IFloodSignal
    {
        /// <summary>This process was answered 429 in <paramref name="chatKey"/> ("" for the whole bot).</summary>
        void Publish(Lane lane, string chatKey, DateTime pausedUntilUtc);

        /// <summary>
        /// Starts listening for pauses other processes publish, handing each to
        /// <paramref name="onPause"/>.
        /// </summary>
        void Subscribe(Action<Lane, string, DateTime> onPause);
    }

    /// <summary>The flood signal in use. A no-op until the cross-process one is built.</summary>
    internal static class FloodSignal
    {
        internal static IFloodSignal Current { get; set; } = new NoFloodSignal();

        internal static void Publish(Lane lane, string chatKey, DateTime pausedUntilUtc)
        {
            try
            {
                Current?.Publish(lane, chatKey, pausedUntilUtc);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Publishing a 429");
            }
        }

        private sealed class NoFloodSignal : IFloodSignal
        {
            public void Publish(Lane lane, string chatKey, DateTime pausedUntilUtc)
            {
            }

            public void Subscribe(Action<Lane, string, DateTime> onPause)
            {
            }
        }
    }
}
