using System;
using System.Linq;
using Enforcer5.Attributes;
using Enforcer5.Enforcement;
using Enforcer5.Helpers;
using Enforcer5.Raids;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Enforcer5
{
    /// <summary>
    /// /raid, /raidmode and the buttons under raid notices and link reports. The work is in
    /// <see cref="RaidGuard"/>.
    /// </summary>
    public static partial class Commands
    {
        /// <summary>/raid turns raid mode on whatever the group's mode; /raid off ends it.</summary>
        [Command(Trigger = "raid", InGroupOnly = true, GroupAdminOnly = true)]
        public static void Raid(Update update, string[] args)
        {
            var chatId = update.Message.Chat.Id;
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            if (string.Equals(args.Length > 1 ? args[1]?.Trim() : null, "off", StringComparison.OrdinalIgnoreCase))
            {
                if (RaidGuard.IncidentIn(chatId) == null)
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "raidNotOn"), update);
                    return;
                }
                RaidGuard.End(chatId, update.Message.From.Id);
                Service.LogCommand(update, update.Message.Text);
                return;
            }
            RaidGuard.Activate(chatId, RaidActions.All, "command", update.Message.From.Id);
            Service.LogCommand(update, update.Message.Text);
        }

        /// <summary>/raidmode human|auto|off: how the group wants raids handled; without an argument, says which.</summary>
        [Command(Trigger = "raidmode", InGroupOnly = true, GroupAdminOnly = true)]
        public static void RaidModeCommand(Update update, string[] args)
        {
            var chatId = update.Message.Chat.Id;
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var wanted = args.Length > 1 ? args[1]?.Trim() : null;
            if (!string.IsNullOrEmpty(wanted))
            {
                if (!Enum.TryParse<RaidMode>(wanted, true, out var mode) || !Enum.IsDefined(mode))
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "raidModeUnknown"), update);
                    return;
                }
                RaidGuard.SetMode(chatId, mode);
                Service.LogCommand(update, update.Message.Text);
            }
            var current = RaidGuard.ModeOf(chatId);
            Bot.SendReply(Methods.GetLocaleString(lang, "raidModeIs", Methods.GetLocaleString(lang, $"raidMode{current}")), update);
        }
    }

    public static partial class CallBacks
    {
        /// <summary>raid:{chat}:{lock|mute|ban|ignore|end|release}, under a raid alert, notice or summary.</summary>
        [Callback(Trigger = "raid", GroupAdminOnly = true)]
        public static void RaidButton(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var action = args.Length > 2 ? args[2] : "";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var admin = call.From.Id;
            switch (action)
            {
                case "lock":
                    RaidGuard.Activate(chatId, RaidActions.All, "button", admin);
                    break;
                case "mute":
                    RaidGuard.Activate(chatId, RaidActions.Quarantine, "button", admin);
                    break;
                case "ban":
                    RaidGuard.Activate(chatId, RaidActions.Quarantine | RaidActions.BanSuspects, "button", admin);
                    break;
                case "ignore":
                    RaidGuard.End(chatId, admin, quietly: true);
                    break;
                case "end":
                    RaidGuard.End(chatId, admin);
                    break;
                case "release":
                    // The joiners the summary was about: still muted until Telegram lifts it, or now.
                    var released = Enforcer5.Helpers.Redis.db.SetMembersAsync($"chat:{chatId}:mutedJoiners").GetAwaiter().GetResult()
                        .Select(v => long.TryParse(v.ToString(), out var id) ? id : 0).Where(id => id != 0).ToList();
                    RaidGuard.Release(chatId, released, admin);
                    break;
            }
            // The buttons have done their job.
            _ = Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "raidDone"));
            if (call.Message != null && action is "lock" or "mute" or "ban" or "ignore" or "release")
                _ = Bot.Api.EditMessageReplyMarkup(call.Message.Chat.Id, call.Message.MessageId, null);
        }

        /// <summary>raidlink:{chat}:{ban|ignore}:{user}:{message}, under a bare-link report.</summary>
        [Callback(Trigger = "raidlink", GroupAdminOnly = true)]
        public static void RaidLinkButton(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var action = args[2];
            var userId = long.Parse(args[3]);
            var messageId = int.Parse(args[4]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            if (action == "ban")
            {
                // Their messages go with them, the link included.
                var done = Methods.BanUser(chatId, userId, lang, "Spam link", call.From.Id, revokeMessages: true);
                if (!done) return;
                Methods.SaveBan(userId, "ban");
            }
            _ = Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "raidDone"));
            // The report itself has said its piece.
            if (call.Message != null)
                _ = EnforcementQueue.SubmitAsync(EnforcementJob.Delete(call.Message.Chat.Id, call.Message.MessageId));
        }
    }
}
