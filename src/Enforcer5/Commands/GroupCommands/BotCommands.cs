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
            var untrustedHere = records.Where(r => !r.IsAllowed).Select(r => r.BotId).ToHashSet();
            var everywhere = BotGuard.Global().Where(g => g.IsAllowed).Select(g => g.BotId)
                .Concat(BotGuard.PremiumTrusted()).Distinct()
                .Where(id => BotGuard.IsTrustedEverywhere(id) && !untrustedHere.Contains(id)).ToList();
            if (records.Count == 0 && everywhere.Count == 0) return Methods.GetLocaleString(lang, "trustedBotsNone");
            var text = new StringBuilder(Methods.GetLocaleString(lang, "trustedBotsTitle"));
            foreach (var group in records.GroupBy(r => r.IsAllowed))
            {
                text.Append("\n\n").Append(Methods.GetLocaleString(lang, group.Key ? "trustedBotsAllowed" : "trustedBotsBlocked"));
                foreach (var record in group.Take(50))
                    text.Append("\n• ").Append(DetectionReports.StoredName(record.BotId).FormatHTML())
                        .Append($" (<code>{record.BotId}</code>): ")
                        .Append(Methods.GetLocaleString(lang, $"botSource_{record.Source}"));
            }
            if (everywhere.Count > 0)
            {
                text.Append("\n\n").Append(Methods.GetLocaleString(lang, "trustedBotsEverywhere"));
                foreach (var botId in everywhere.Take(50))
                    text.Append("\n• ").Append(DetectionReports.StoredName(botId).FormatHTML()).Append($" (<code>{botId}</code>)");
            }
            return text.ToString();
        }

        /// <summary>/globaltrustbot: the bot is let be in every group, unchecked, as the game's bots are (Detection/BotGuard).</summary>
        [Command(Trigger = "globaltrustbot", DevOnly = true, Description = "Trust a bot in every group: reply, id or @username")]
        public static void GlobalTrustBot(Update update, string[] args) => GlobalBotCommand(update, args, "trust");

        /// <summary>/globaluntrustbot: the bot is removed from every group it posts in or is added to.</summary>
        [Command(Trigger = "globaluntrustbot", DevOnly = true, Description = "Remove a bot from every group: reply, id or @username")]
        public static void GlobalUntrustBot(Update update, string[] args) => GlobalBotCommand(update, args, "untrust");

        /// <summary>/globalforgetbot: forgets the decision for every group; each group's own counts again.</summary>
        [Command(Trigger = "globalforgetbot", DevOnly = true, Description = "Forget a bot's trust or block in every group")]
        public static void GlobalForgetBot(Update update, string[] args) => GlobalBotCommand(update, args, "forget");

        /// <summary>/globalbots: the bots trusted or blocked in every group, and those premium groups trust.</summary>
        [Command(Trigger = "globalbots", DevOnly = true, Description = "Bots trusted or blocked in every group")]
        public static void GlobalBots(Update update, string[] args) => Bot.SendReply(GlobalBotsText(), update);

        internal static string GlobalBotsText()
        {
            var global = BotGuard.Global();
            var premium = BotGuard.PremiumTrusted().Where(id => global.All(g => g.BotId != id)).ToList();
            if (global.Count == 0 && premium.Count == 0) return "No bot is trusted or blocked in every group.";
            var text = new StringBuilder("<b>Bots in every group</b>");
            foreach (var group in global.GroupBy(g => g.IsAllowed))
            {
                text.Append(group.Key ? "\n\nTrusted by a dev:" : "\n\nBlocked by a dev:");
                foreach (var record in group.Take(100))
                    text.Append("\n• ").Append(DetectionReports.StoredName(record.BotId).FormatHTML())
                        .Append($" (<code>{record.BotId}</code>), {record.SetAt:yyyy-MM-dd}");
            }
            if (premium.Count > 0)
            {
                text.Append("\n\nTrusted by a premium group:");
                foreach (var botId in premium.Take(100))
                    text.Append("\n• ").Append(DetectionReports.StoredName(botId).FormatHTML()).Append($" (<code>{botId}</code>)");
            }
            return text.ToString();
        }

        private static void GlobalBotCommand(Update update, string[] args, string op)
        {
            var given = args.Length > 1 ? args[1]?.Trim() : null;
            long botId;
            string name;
            if (update.Message.ReplyToMessage?.From is { } from)
            {
                if (!from.IsBot || from.Id == Bot.Me.Id)
                {
                    Bot.SendReply("That is not another bot.", update);
                    return;
                }
                (botId, name) = (from.Id, from.Username != null ? $"@{from.Username}" : from.FirstName);
            }
            else if (long.TryParse(given, out var id))
                (botId, name) = (id, DetectionReports.StoredName(id));
            else if (given != null && given.StartsWith("@") && given.EndsWith("bot", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    botId = Methods.ResolveIdFromusername(given);
                }
                catch (Exception e) when (e.Message == "UnableToResolveUsername")
                {
                    // Never seen by the bot: the dev gives the id instead.
                    Bot.SendReply($"I don't know {given.FormatHTML()} yet. Give its id instead.", update);
                    return;
                }
                name = given;
            }
            else
            {
                Bot.SendReply($"Reply to the bot's message, or give its id or @username: /global{op}bot @examplebot", update);
                return;
            }
            if (botId == Bot.Me.Id)
            {
                Bot.SendReply("That is me.", update);
                return;
            }
            var devId = update.Message.From.Id;
            switch (op)
            {
                case "trust":
                    BotGuard.SetGlobalAsync(botId, allowed: true, devId).GetAwaiter().GetResult();
                    Bot.SendReply($"{name.FormatHTML()} (<code>{botId}</code>) is trusted in every group: not checked, and our bans of it are lifted. A group's own /untrustbot still removes it there.", update);
                    break;
                case "untrust":
                    BotGuard.SetGlobalAsync(botId, allowed: false, devId).GetAwaiter().GetResult();
                    Bot.SendReply($"{name.FormatHTML()} (<code>{botId}</code>) is removed from every group it posts in or is added to, whoever trusts it there.", update);
                    break;
                default:
                    BotGuard.ForgetGlobalAsync(botId).GetAwaiter().GetResult();
                    Bot.SendReply($"{name.FormatHTML()} (<code>{botId}</code>): each group's own decision counts again.", update);
                    break;
            }
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
        /// The bot the admin means: the inline bot the message they replied to was sent through, else its sender, or the
        /// id or @username given, as Telegram knows it in the group. A guest bot was never in the group; a username ending
        /// in "bot" (as every bot's does) or a bot the group already has a record of is taken as one.
        /// </summary>
        private static User TargetBot(Update update, string[] args)
        {
            // A member's post through an inline bot: its sender is the member, the bot is the one meant.
            if (update.Message.ReplyToMessage?.ViaBot is { } via) return via;
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
