using System;
using System.Linq;
using Telegram.Bot.Types;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Whether a message from a bot account is handled at all, and which commands a bot may run. People
    /// always are handled. Bots only when trusted: with Bot-to-Bot Communication Mode on, the bot
    /// receives every other bot's messages in its groups, and would otherwise run their commands and
    /// moderate them like a person's. Same rule as blackwolf's BotSenderFilter.
    /// </summary>
    internal static class BotSenderFilter
    {
        /// <summary>@esmehelbot, the relay bot that posts language uploads in the translators group.</summary>
        internal const long EsmeBotId = 8783324558;

        /// <summary>Bots whose messages are handled like a person's; every other bot is ignored.</summary>
        internal static readonly long[] TrustedBotIds =
        {
            EsmeBotId,
            1087968824, // @GroupAnonymousBot: a group admin posting anonymously
            136817688,  // @Channel_Bot: a person posting as a channel (see Models/ChannelPosts)
        };

        /// <summary>
        /// Commands Esme may run without being a lang admin. She is deliberately not in <c>langAdmins</c>:
        /// that set also allows /makelangadmin and /removelangadmin.
        /// </summary>
        internal static readonly string[] EsmeCommands = { "uploadlanguage" };

        internal static bool Accept(Message message) => Accept(message?.From);

        internal static bool Accept(User sender) =>
            sender == null || !sender.IsBot || TrustedBotIds.Contains(sender.Id);

        /// <summary>Whether a sender who lacks the command's rights may still run it: only Esme, only <see cref="EsmeCommands"/>.</summary>
        internal static bool IsGrantedCommand(long senderId, string trigger) =>
            senderId == EsmeBotId && EsmeCommands.Contains(trigger ?? "", StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The first word of a command: its trigger and whether it is for this bot. <c>/cmd@SomeBot</c> is for
    /// that bot only (compared without case, as Telegram does); a plain <c>/cmd</c> is for every bot in the chat.
    /// </summary>
    internal readonly record struct CommandAddress(string Trigger, bool Addressed, bool ForMe)
    {
        internal static CommandAddress Parse(string firstWord, string myUsername)
        {
            firstWord ??= "";
            var at = firstWord.IndexOf('@');
            if (at < 0) return new CommandAddress(firstWord, false, true);
            var target = firstWord.Substring(at + 1);
            return new CommandAddress(firstWord.Substring(0, at), true,
                string.Equals(target, myUsername, StringComparison.OrdinalIgnoreCase));
        }
    }
}
