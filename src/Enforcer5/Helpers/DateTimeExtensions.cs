using System;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Replaces the Telegram.Bot.Helpers extensions that the old 13.x library provided
    /// and 22.x no longer ships.
    /// </summary>
    public static class DateTimeExtensions
    {
        /// <summary>
        /// Seconds since the Unix epoch. Values with an unspecified Kind are treated as UTC,
        /// which is what every caller here passes.
        /// </summary>
        public static long ToUnixTime(this DateTime dateTime)
        {
            if (dateTime == DateTime.MinValue)
                return 0;

            var utc = dateTime.Kind == DateTimeKind.Local
                ? dateTime.ToUniversalTime()
                : DateTime.SpecifyKind(dateTime, DateTimeKind.Utc);

            return new DateTimeOffset(utc).ToUnixTimeSeconds();
        }

        // The inverse, FromUnixTime, already lives on Methods - do not duplicate it here.
    }
}
