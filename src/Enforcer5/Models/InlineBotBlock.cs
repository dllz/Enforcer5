using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Enforcer5.Helpers;

namespace Enforcer5.Models
{
    internal enum InlineBotBlockKind
    {
        /// <summary>Exact bot username, compared case-insensitively.</summary>
        Username,

        /// <summary>Regular expression tested against the bot username.</summary>
        Pattern
    }

    internal enum InlineBotBlockParseResult
    {
        Ok,
        Empty,
        InvalidUsername,
        PatternTooLong,
        InvalidPattern
    }

    /// <summary>
    /// One entry on a chat's inline bot blocklist, matched against <c>Message.ViaBot.Username</c>.
    ///
    /// Admins write an entry as <c>@username</c> (or bare <c>username</c>) for an exact match, or
    /// as <c>/regex/</c> for a pattern. <see cref="ToString"/> renders that same form, so whatever
    /// /blockedinline lists can be pasted straight into /unblockinline.
    ///
    /// Usernames rather than ids, because that is what admins know and the Bot API cannot resolve
    /// a bot's username to its id. A bot that changes its username drops off the list.
    /// </summary>
    internal sealed record InlineBotBlock(InlineBotBlockKind Kind, string Value)
    {
        // Official inline bots have short usernames (@gif, @vid, @pic), so neither the usual
        // five-character minimum nor the "bot" suffix can be required.
        private static readonly Regex UsernameFormat = new Regex(
            "^[A-Za-z][A-Za-z0-9_]{2,31}$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Parses admin input. A value wrapped in slashes is a pattern, anything else a username
        /// (usernames cannot contain a slash, so the two never collide). For an invalid pattern
        /// <paramref name="detail"/> carries the reason.
        /// </summary>
        public static InlineBotBlockParseResult TryParse(string input, out InlineBotBlock block, out string detail)
        {
            block = null;
            detail = null;
            var text = input?.Trim();
            if (string.IsNullOrEmpty(text)) return InlineBotBlockParseResult.Empty;

            if (text.Length >= 2 && text[0] == '/' && text[^1] == '/')
            {
                var pattern = text.Substring(1, text.Length - 2);
                if (pattern.Length == 0)
                {
                    // An empty pattern matches every bot. Make the admin say so explicitly (/.*/).
                    detail = "the expression is empty";
                    return InlineBotBlockParseResult.InvalidPattern;
                }
                if (pattern.Length > InlineBotPattern.MaxLength) return InlineBotBlockParseResult.PatternTooLong;
                if (!InlineBotPattern.TryValidate(pattern, out detail)) return InlineBotBlockParseResult.InvalidPattern;

                block = new InlineBotBlock(InlineBotBlockKind.Pattern, pattern);
                return InlineBotBlockParseResult.Ok;
            }

            var username = text.StartsWith("@") ? text.Substring(1) : text;
            if (!UsernameFormat.IsMatch(username)) return InlineBotBlockParseResult.InvalidUsername;

            // Telegram usernames are case-insensitive; store one canonical form so the same bot
            // cannot be listed twice.
            block = new InlineBotBlock(InlineBotBlockKind.Username, username.ToLowerInvariant());
            return InlineBotBlockParseResult.Ok;
        }

        /// <summary>
        /// Whether this entry blocks the bot with <paramref name="botUsername"/> (no leading @).
        /// Throws if a stored pattern cannot be evaluated; see <see cref="FirstMatch"/>.
        /// </summary>
        public bool Matches(string botUsername)
        {
            if (string.IsNullOrEmpty(botUsername)) return false;
            return Kind == InlineBotBlockKind.Pattern
                ? InlineBotPattern.IsMatch(Value, botUsername)
                : string.Equals(Value, botUsername, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The first entry that blocks <paramref name="botUsername"/>, or null. An entry that cannot
        /// be evaluated is logged and skipped, so one bad pattern does not disable the whole list.
        /// </summary>
        public static InlineBotBlock FirstMatch(IEnumerable<InlineBotBlock> blocks, string botUsername)
        {
            if (blocks == null || string.IsNullOrEmpty(botUsername)) return null;
            foreach (var block in blocks)
            {
                if (SafeMatches(block, botUsername)) return block;
            }
            return null;
        }

        /// <summary>
        /// Every entry that blocks <paramref name="botUsername"/>, in list order, for /testinline.
        /// Entries that cannot be evaluated are skipped as in <see cref="FirstMatch"/>.
        /// </summary>
        public static IReadOnlyList<InlineBotBlock> AllMatches(IEnumerable<InlineBotBlock> blocks, string botUsername)
        {
            var matches = new List<InlineBotBlock>();
            if (blocks == null || string.IsNullOrEmpty(botUsername)) return matches;
            foreach (var block in blocks)
            {
                if (SafeMatches(block, botUsername)) matches.Add(block);
            }
            return matches;
        }

        private static bool SafeMatches(InlineBotBlock block, string botUsername)
        {
            try
            {
                return block.Matches(botUsername);
            }
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException ||
                                      e is RegexMatchTimeoutException)
            {
                LogHelper.Error($"Inline bot block {block} could not be evaluated: {e.Message}");
                return false;
            }
        }

        public override string ToString() =>
            Kind == InlineBotBlockKind.Pattern ? $"/{Value}/" : $"@{Value}";
    }
}
