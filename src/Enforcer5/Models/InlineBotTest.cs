using System;
using System.Collections.Generic;

namespace Enforcer5.Models
{
    internal enum InlineBotTestParseResult
    {
        Ok,
        Empty,
        InvalidUsername,
        TooManyUsernames,
        PatternTooLong,
        InvalidPattern
    }

    /// <summary>
    /// What /testinline was asked to check: one or more bot usernames, and optionally a pattern to
    /// try instead of the chat's blocklist.
    ///
    /// Input is <c>@bot [@bot ...]</c> to test against the blocklist, or <c>/regex/ @bot [@bot ...]</c>
    /// to test a pattern without saving it. A pattern runs from the first slash to the last one;
    /// usernames cannot contain a slash, so a pattern may contain spaces. With no usernames, the
    /// bot behind the replied-to message is used.
    /// </summary>
    internal sealed class InlineBotTest
    {
        /// <summary>Bounds the reply length and the work one command can ask for.</summary>
        internal const int MaxUsernames = 10;

        private InlineBotTest(InlineBotBlock pattern, IReadOnlyList<string> usernames)
        {
            Pattern = pattern;
            Usernames = usernames;
        }

        /// <summary>The pattern to try, or null to test against the chat's blocklist.</summary>
        public InlineBotBlock Pattern { get; }

        /// <summary>Bot usernames to test: lowercase, without the @, no duplicates.</summary>
        public IReadOnlyList<string> Usernames { get; }

        /// <summary>
        /// Parses the command argument. <paramref name="repliedBotUsername"/> is the username of the
        /// bot behind the replied-to message, if any. On failure <paramref name="detail"/> holds the
        /// offending username or the reason the pattern was rejected.
        /// </summary>
        public static InlineBotTestParseResult TryParse(string input, string repliedBotUsername,
            out InlineBotTest test, out string detail)
        {
            test = null;
            detail = null;
            var text = input?.Trim() ?? "";

            InlineBotBlock pattern = null;
            var lastSlash = text.LastIndexOf('/');
            if (text.StartsWith("/") && lastSlash > 0)
            {
                var patternText = text.Substring(0, lastSlash + 1);
                switch (InlineBotBlock.TryParse(patternText, out pattern, out detail))
                {
                    case InlineBotBlockParseResult.Ok:
                        break;
                    case InlineBotBlockParseResult.PatternTooLong:
                        return InlineBotTestParseResult.PatternTooLong;
                    default:
                        return InlineBotTestParseResult.InvalidPattern;
                }
                text = text.Substring(lastSlash + 1);
            }

            var tokens = text.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0 && !string.IsNullOrEmpty(repliedBotUsername))
            {
                tokens = new[] { repliedBotUsername };
            }
            if (tokens.Length == 0) return InlineBotTestParseResult.Empty;
            if (tokens.Length > MaxUsernames) return InlineBotTestParseResult.TooManyUsernames;

            var usernames = new List<string>(tokens.Length);
            foreach (var token in tokens)
            {
                if (InlineBotBlock.TryParse(token, out var parsed, out _) != InlineBotBlockParseResult.Ok ||
                    parsed.Kind != InlineBotBlockKind.Username)
                {
                    detail = token;
                    return InlineBotTestParseResult.InvalidUsername;
                }
                if (!usernames.Contains(parsed.Value)) usernames.Add(parsed.Value);
            }

            test = new InlineBotTest(pattern, usernames);
            return InlineBotTestParseResult.Ok;
        }
    }
}
