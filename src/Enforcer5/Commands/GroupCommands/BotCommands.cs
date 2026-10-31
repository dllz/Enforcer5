using System;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Data;
using Enforcer5.Detection;
using Enforcer5.Helpers;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Enforcer5
{
    public static partial class Commands
    {
        /// <summary>/trustbot: the bot may post and answer here; a ban of ours is lifted (Detection/BotGuard).</summary>
        [Command(Trigger = "trustbot", InGroupOnly = true, GroupAdminOnly = true)]
        public static void TrustBot(Update update, string[] args) => BotTrustCommand(update, args, trust: true);

        /// <summary>/untrustbot: the bot is banned and anything it posts here deleted.</summary>
        [Command(Trigger = "untrustbot", InGroupOnly = true, GroupAdminOnly = true)]
        public static void UntrustBot(Update update, string[] args) => BotTrustCommand(update, args, trust: false);

        /// <summary>/trustedbots: the bots this group trusts or removed, and how.</summary>
        [Command(Trigger = "trustedbots", InGroupOnly = true, GroupAdminOnly = true)]
        public static void TrustedBots(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            Bot.SendReply(TrustedBotsText(update.Message.Chat.Id, lang), update);
        }

        internal static string TrustedBotsText(long chatId, XDocument lang)
        {
            var records = BotGuard.ForChat(chatId);
            if (records.Count == 0) return Methods.GetLocaleString(lang, "trustedBotsNone");
            var text = new StringBuilder(Methods.GetLocaleString(lang, "trustedBotsTitle"));
            foreach (var group in records.GroupBy(r => r.IsAllowed))
            {
                text.Append("\n\n").Append(Methods.GetLocaleString(lang, group.Key ? "trustedBotsAllowed" : "trustedBotsBlocked"));
                foreach (var record in group.Take(50))
                    text.Append("\n• ").Append(DetectionReports.StoredName(record.BotId).FormatHTML())
                        .Append($" (<code>{record.BotId}</code>): ")
                        .Append(Methods.GetLocaleString(lang, $"botSource_{record.Source}"));
            }
            return text.ToString();
        }

        private static void BotTrustCommand(Update update, string[] args, bool trust)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var chatId = update.Message.Chat.Id;
            var trigger = trust ? "trustbot" : "untrustbot";
            if (update.Message.ReplyToMessage == null && string.IsNullOrWhiteSpace(args.Length > 1 ? args[1] : null))
            {
                Bot.SendReply(Methods.GetLocaleString(lang, $"hcommand{trigger}"), update);
                return;
            }
            User bot;
            try
            {
                bot = TargetBot(update, args);
            }
            catch (Exception e)
            {
                // An unknown @username is the admin's typo, not a bug.
                if (e.Message != "UnableToResolveUsername")
                    ErrorReporter.Report(e, $"Resolving the bot for /{trigger}");
                Methods.SendError(e.Message, update.Message, lang);
                return;
            }
            if (bot == null || !bot.IsBot || bot.Id == Bot.Me.Id)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "trustBotNotABot"), update);
                return;
            }
            var name = bot.Username != null ? $"@{bot.Username}" : bot.FirstName ?? bot.Id.ToString();
            if (BotGuard.IsKnown(bot))
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "trustBotKnown", name), update);
                return;
            }
            if (trust) BotGuard.TrustAsync(chatId, bot.Id, update.Message.From.Id).GetAwaiter().GetResult();
            else BotGuard.UntrustAsync(chatId, bot.Id, update.Message.From.Id).GetAwaiter().GetResult();
            Bot.SendReply(Methods.GetLocaleString(lang, trust ? "trustBotDone" : "untrustBotDone", name), update);
            Service.LogCommand(update, update.Message.Text);
        }

        /// <summary>
        /// The bot the admin means: the sender of the message they replied to, or the id or @username given, as
        /// Telegram knows it in the group. A guest bot was never in the group; a username ending in "bot" (as
        /// every bot's does) or a bot the group already has a record of is taken as one.
        /// </summary>
        private static User TargetBot(Update update, string[] args)
        {
            if (update.Message.ReplyToMessage?.From is { } from) return from;
            var chatId = update.Message.Chat.Id;
            var id = Methods.GetUserId(update, args);
            try
            {
                var member = Bot.Api.GetChatMember(chatId, id).GetAwaiter().GetResult();
                if (member?.User != null) return member.User;
            }
            catch (Exception e) when (Bot.AsApiError(e)?.ErrorCode == 400)
            {
                // Never in the group: a guest bot, or a person. Decided below.
            }
            var given = args.Length > 1 ? args[1].Trim().TrimStart('@') : "";
            var looksLikeBot = given.EndsWith("bot", StringComparison.OrdinalIgnoreCase) || BotGuard.Get(chatId, id) != null;
            return looksLikeBot ? new User { Id = id, IsBot = true, FirstName = given, Username = given.EndsWith("bot", StringComparison.OrdinalIgnoreCase) ? given : null } : null;
        }
    }
}
