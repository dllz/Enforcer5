using System;
using System.Linq;
using System.Text.RegularExpressions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Models
{
    internal enum DurationParseResult
    {
        Ok,

        /// <summary>No duration given; the caller falls back to the group default.</summary>
        Empty,

        /// <summary>Not a number, or a unit we do not know.</summary>
        Invalid,

        /// <summary>Shorter than <see cref="Duration.Minimum"/> or longer than <see cref="Duration.Maximum"/>.</summary>
        OutOfRange
    }

    /// <summary>
    /// How long a temporary restriction lasts, as admins type it: <c>30</c>, <c>30 min</c>,
    /// <c>2 hours</c>, <c>1 day</c>, or the short forms <c>30m</c>, <c>2h</c>, <c>1d</c>. A bare
    /// number is minutes. Pure, so it is tested directly.
    /// </summary>
    internal static class Duration
    {
        /// <summary>Default for /tempmute and /tempban when the group has set none: one day.</summary>
        internal const int DefaultMinutes = 1440;

        internal static readonly TimeSpan Minimum = TimeSpan.FromMinutes(1);

        /// <summary>
        /// Telegram treats an <c>untilDate</c> more than 366 days ahead (or under 30 seconds) as
        /// forever, so a longer "temporary" restriction would silently become permanent.
        /// </summary>
        internal static readonly TimeSpan Maximum = TimeSpan.FromDays(365);

        private static readonly Regex Format = new Regex(@"^(\d{1,9})\s*([a-z]*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        internal static DurationParseResult TryParse(string text, out TimeSpan duration)
        {
            duration = TimeSpan.Zero;
            if (string.IsNullOrWhiteSpace(text)) return DurationParseResult.Empty;

            var match = Format.Match(text.Trim());
            if (!match.Success) return DurationParseResult.Invalid;

            var amount = long.Parse(match.Groups[1].Value);
            // A million of the smallest unit is already past Maximum, and a larger number of days
            // would overflow TimeSpan.
            var tooLarge = amount > 1_000_000;
            switch (match.Groups[2].Value.ToLowerInvariant())
            {
                case "":
                case "m":
                case "min":
                case "mins":
                case "minute":
                case "minutes":
                    if (tooLarge) return DurationParseResult.OutOfRange;
                    duration = TimeSpan.FromMinutes(amount);
                    break;
                case "h":
                case "hr":
                case "hrs":
                case "hour":
                case "hours":
                    if (tooLarge) return DurationParseResult.OutOfRange;
                    duration = TimeSpan.FromHours(amount);
                    break;
                case "d":
                case "day":
                case "days":
                    if (tooLarge) return DurationParseResult.OutOfRange;
                    duration = TimeSpan.FromDays(amount);
                    break;
                default:
                    return DurationParseResult.Invalid;
            }

            return duration < Minimum || duration > Maximum ? DurationParseResult.OutOfRange : DurationParseResult.Ok;
        }

        /// <summary>
        /// A group's stored default, in minutes. Unset, unreadable, zero or negative values give
        /// <see cref="DefaultMinutes"/>. The old reader handed back the 0 that a failed
        /// int.TryParse leaves behind, so in groups without a default /tempmute muted forever and
        /// every tempban was lifted again by the next checker run.
        /// </summary>
        internal static int StoredMinutesOrDefault(string stored, int maxMinutes = int.MaxValue)
        {
            if (!int.TryParse(stored, out var minutes)) return DefaultMinutes;
            return minutes > 0 && minutes <= maxMinutes ? minutes : DefaultMinutes;
        }

        internal static int MaximumMinutes => (int)Maximum.TotalMinutes;

        /// <summary>
        /// As days:hours:minutes, which is how the tempban and tempmute messages present it. Rounded
        /// up to the minute, so a restriction with seconds left does not show as 00:00:00.
        /// </summary>
        internal static string ToDisplay(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
            var minutes = (long)Math.Ceiling(duration.TotalMinutes);
            return $"{minutes / 1440:00}:{minutes / 60 % 24:00}:{minutes % 60:00}";
        }

        /// <summary>
        /// The part of a /tempmute argument that holds the duration. With a reply the whole argument
        /// is the duration. Otherwise the target comes first: either a text mention, which can span
        /// several words, or a single word (an id or @username).
        /// </summary>
        internal static string AfterTarget(Message command, string argument)
        {
            if (string.IsNullOrWhiteSpace(argument)) return null;
            if (command?.ReplyToMessage != null) return argument.Trim();

            var mention = command?.Entities?.FirstOrDefault(e => e.Type == MessageEntityType.TextMention);
            if (mention != null && command.Text != null && mention.Offset + mention.Length <= command.Text.Length)
                return command.Text.Substring(mention.Offset + mention.Length).Trim();

            var trimmed = argument.Trim();
            var space = trimmed.IndexOf(' ');
            return space < 0 ? null : trimmed.Substring(space + 1).Trim();
        }
    }
}
