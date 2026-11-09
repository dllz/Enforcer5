using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Types;

namespace Enforcer5
{
    /// <summary>
    /// /testinline: whether the group's inline bot blocklist (/block @bot or /block /regex/) blocks a bot, or a regular
    /// expression would. Enforcement lives in OnMessage.BlockedInlineBot and storage behind <see cref="IInlineBotBlockRepository"/>.
    /// </summary>
    public static partial class Commands
    {
        // Admin-only for the same reason as /blocked: it reveals what the blocklist matches.
        [Command(Trigger = "testinline", InGroupOnly = true, GroupAdminOnly = true)]
        public static void TestInlineBot(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var input = args.Length > 1 ? args[1] : null;
            var repliedBot = update.Message.ReplyToMessage?.ViaBot?.Username;

            string reply;
            switch (InlineBotTest.TryParse(input, repliedBot, out var test, out var detail))
            {
                case InlineBotTestParseResult.Ok:
                    var blocks = test.Pattern == null
                        ? Repositories.InlineBotBlocks.GetAsync(update.Message.Chat.Id).GetAwaiter().GetResult()
                        : null;
                    reply = InlineBotTestReply(lang, test, blocks);
                    break;
                case InlineBotTestParseResult.InvalidUsername:
                    reply = Methods.GetLocaleString(lang, "inlineBlockInvalidUsername", detail);
                    break;
                case InlineBotTestParseResult.TooManyUsernames:
                    reply = Methods.GetLocaleString(lang, "inlineTestTooMany", InlineBotTest.MaxUsernames);
                    break;
                case InlineBotTestParseResult.PatternTooLong:
                    reply = Methods.GetLocaleString(lang, "inlineBlockPatternTooLong", InlineBotPattern.MaxLength);
                    break;
                case InlineBotTestParseResult.InvalidPattern:
                    reply = Methods.GetLocaleString(lang, "inlineBlockInvalidPattern", detail);
                    break;
                default:
                    reply = Methods.GetLocaleString(lang, "inlineTestUsage", InlineBotTest.MaxUsernames);
                    break;
            }
            Bot.SendReply(reply, update);
        }

        /// <summary>
        /// The /testinline answer: one line per username. With a pattern, whether it matches;
        /// otherwise which of the chat's <paramref name="blocks"/> block that bot. Every argument
        /// goes through GetLocaleString, which escapes it for HTML.
        /// </summary>
        internal static string InlineBotTestReply(XDocument lang, InlineBotTest test, IReadOnlyList<InlineBotBlock> blocks)
        {
            if (test.Pattern == null && (blocks == null || blocks.Count == 0))
                return Methods.GetLocaleString(lang, "inlineBlockListEmpty");

            var lines = new List<string>(test.Usernames.Count + 1);
            if (test.Pattern != null)
                lines.Add(Methods.GetLocaleString(lang, "inlineTestPatternNote", test.Pattern));

            foreach (var username in test.Usernames)
            {
                var bot = $"@{username}";
                if (test.Pattern != null)
                {
                    var matched = InlineBotBlock.AllMatches(new[] { test.Pattern }, username).Count > 0;
                    lines.Add(Methods.GetLocaleString(lang, matched ? "inlineTestMatch" : "inlineTestNoMatch", bot));
                    continue;
                }

                var matches = InlineBotBlock.AllMatches(blocks, username);
                lines.Add(matches.Count == 0
                    ? Methods.GetLocaleString(lang, "inlineTestNotBlocked", bot)
                    : Methods.GetLocaleString(lang, "inlineTestBlockedBy", bot, string.Join(", ", matches)));
            }
            return string.Join("\n", lines);
        }
    }
}
