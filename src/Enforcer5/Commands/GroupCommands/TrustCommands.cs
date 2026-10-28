using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Data;
using Enforcer5.Detection;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Enforcer5.Storage;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5
{
    /// <summary>
    /// /untrust and /trust, their /user buttons, and the "Learned from you" screen (Detection build step 7b,
    /// <see cref="TrustLearning"/>).
    /// </summary>
    public static partial class Commands
    {
        /// <summary>/untrust: the member counts as new until an admin restores them. An established one also raises the group's level.</summary>
        [Command(Trigger = "untrust", InGroupOnly = true, GroupAdminOnly = true)]
        public static void Untrust(Update update, string[] args) => TrustCommand(update, args, untrust: true);

        /// <summary>/trust: undoes /untrust; the member counts by their messages and days again.</summary>
        [Command(Trigger = "trust", InGroupOnly = true, GroupAdminOnly = true)]
        public static void Trust(Update update, string[] args) => TrustCommand(update, args, untrust: false);

        private static void TrustCommand(Update update, string[] args, bool untrust)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            long userId;
            try
            {
                if (update.Message.ReplyToMessage == null && string.IsNullOrWhiteSpace(args.Length > 1 ? args[1] : null))
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, untrust ? "hcommanduntrust" : "hcommandtrust"), update);
                    return;
                }
                userId = Methods.GetUserId(update, args);
            }
            catch (Exception e)
            {
                // An unknown @username is the admin's typo, not a bug.
                if (e.Message != "UnableToResolveUsername")
                    ErrorReporter.Report(e, $"Resolving the user for /{(untrust ? "untrust" : "trust")}");
                Methods.SendError(e.Message, update.Message, lang);
                return;
            }
            Bot.SendReply(ChangeTrust(update.Message.Chat.Id, userId, update.Message.From.Id, untrust, lang), update);
            Service.LogCommand(update, update.Message.Text);
        }

        /// <summary>Sets the member back to new or restores them; the answer for the admin.</summary>
        internal static string ChangeTrust(long chatId, long userId, long adminId, bool untrust, XDocument lang)
        {
            var name = DetectionReports.StoredName(userId);
            if (untrust)
            {
                if (Methods.IsGroupAdmin(userId, chatId) || userId == Bot.Me.Id) return Methods.GetLocaleString(lang, "untrustAdmin");
                var before = MemberTrust.OfAsync(chatId, userId).GetAwaiter().GetResult();
                var set = TrustOverrides.SetAsync(chatId, userId, adminId).GetAwaiter().GetResult();
                if (set == null) return Methods.GetLocaleString(lang, "failed");
                if (set == false) return Methods.GetLocaleString(lang, "untrustAlready", name);
                // Someone the group's level called established: it was too low for this group.
                if (before == TrustLevel.Established)
                    Bot.Dispatch(async () =>
                    {
                        try
                        {
                            await TrustLearning.RaiseAsync(chatId, userId, "untrust", Array.Empty<string>(), null, "untrust");
                        }
                        catch (Exception e)
                        {
                            ErrorReporter.Report(e, $"Learning from the /untrust of {userId} in {chatId}");
                        }
                    }, "TrustLearning.Untrust");
                return Methods.GetLocaleString(lang, "untrustDone", name);
            }
            var removed = TrustOverrides.RemoveAsync(chatId, userId).GetAwaiter().GetResult();
            if (removed == null) return Methods.GetLocaleString(lang, "failed");
            return Methods.GetLocaleString(lang, removed == true ? "trustDone" : "trustNotSet", name);
        }

        /// <summary>The Learned from you screen: the group's levels against the usual ones, the last changes, and the switch.</summary>
        internal static string LearnMenuText(long chatId, XDocument lang)
        {
            var engine = DetectionSettings.For(chatId).Engine;
            var usual = MemberTrust.UsualThresholds(chatId);
            var current = TrustLearning.Effective(engine, usual);
            var established = current == usual
                ? Methods.GetLocaleString(lang, "learnUsual")
                : Methods.GetLocaleString(lang, "learnLearned", usual.Messages, usual.Days);
            var report = Methods.GetLocaleString(lang, engine.ReportAt == EngineSettings.DefaultReportAt ? "learnReportDefault" : "learnReportLearned");
            var text = Methods.GetLocaleStringNoFormat(lang, "learnMenu", current.Messages, current.Days, established.FormatHTML(), engine.ReportAt,
                report.FormatHTML(), TrustOverrides.CountIn(chatId), Methods.GetLocaleString(lang, engine.Learn ? "on" : "off").FormatHTML());
            if (!PostgresStorage.Ready) return text;
            try
            {
                var recent = Repositories.DetectionLearning.RecentAdjustmentsAsync(chatId, 5).GetAwaiter().GetResult();
                if (recent.Count > 0)
                    text += "\n\n" + Methods.GetLocaleString(lang, "learnHistoryHeader") + "\n" +
                            string.Join("\n", recent.Select(a => "• " + HistoryLine(lang, a, usual).FormatHTML()));
            }
            catch (Exception e)
            {
                // The screen without the history.
                ErrorReporter.Report(e, $"Reading what Detection learned in {chatId}");
            }
            return text;
        }

        internal static string HistoryLine(XDocument lang, Adjustment a, (int Messages, int Days) usual)
        {
            var date = a.MadeAt.UtcDateTime.ToString("d MMM", CultureInfo.InvariantCulture);
            string Level(int? m, int? d) => a.Kind == Adjustment.ReportAt
                ? (m ?? EngineSettings.DefaultReportAt).ToString()
                : $"{Math.Max(usual.Messages, m ?? 0)}/{Math.Max(usual.Days, d ?? 0)}";
            var line = Methods.GetLocaleStringNoFormat(lang, $"learnHistory_{a.Kind}_{a.Reason}", date, Level(a.FromValue, a.FromDays),
                Level(a.ToValue, a.ToDays));
            if (a.UndoneAt != null) line += Methods.GetLocaleStringNoFormat(lang, "learnUndoneMark");
            else if (!a.Applied && a.Reason != "takeover") line += Methods.GetLocaleStringNoFormat(lang, "learnProposedMark");
            return line;
        }

        internal static InlineKeyboardMarkup GenLearnMenu(long chatId, XDocument lang)
        {
            var engine = DetectionSettings.For(chatId).Engine;
            var menu = new Menu(1);
            menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "learnToggleButton", Methods.GetLocaleString(lang, engine.Learn ? "on" : "off")),
                $"learntoggle:{chatId}") { Style = engine.Learn ? KeyboardButtonStyle.Success : null });
            menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "learnUndoAllButton"), $"learnreset:{chatId}") { Style = KeyboardButtonStyle.Danger });
            menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "backButton"), $"detectionmenu:{chatId}"));
            return Key.CreateMarkupFromMenu(menu);
        }
    }

    public static partial class CallBacks
    {
        private static void ShowLearnMenu(CallbackQuery call, long chatId, XDocument lang) =>
            Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, Commands.LearnMenuText(chatId, lang), ParseMode.Html,
                replyMarkup: Commands.GenLearnMenu(chatId, lang)).GetAwaiter().GetResult();

        [Callback(Trigger = "learnmenu", GroupAdminOnly = true)]
        public static void LearnMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            ShowLearnMenu(call, chatId, Methods.GetGroupLanguage(chatId).Doc);
        }

        [Callback(Trigger = "learntoggle", GroupAdminOnly = true)]
        public static void LearnToggle(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            if (DetectionSettings.TryUpdate(chatId, p => p with { Engine = p.Engine with { Learn = !p.Engine.Learn } }) == null)
            {
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "failed"));
                return;
            }
            Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            ShowLearnMenu(call, chatId, lang);
        }

        [Callback(Trigger = "learnreset", GroupAdminOnly = true)]
        public static void LearnReset(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var done = TrustLearning.ResetAsync(chatId).GetAwaiter().GetResult();
            Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, done ? "learnUndoAllDone" : "failed"));
            if (done) ShowLearnMenu(call, chatId, lang);
        }

        /// <summary>Set back to new, or restore, from /user: userbuttonuntrust / userbuttontrust:{chat}:{user}.</summary>
        [Callback(Trigger = "userbuttonuntrust", GroupAdminOnly = true)]
        public static void UserButtonUntrust(CallbackQuery call, string[] args) => UserTrustButton(call, args, true);

        [Callback(Trigger = "userbuttontrust", GroupAdminOnly = true)]
        public static void UserButtonTrust(CallbackQuery call, string[] args) => UserTrustButton(call, args, false);

        private static void UserTrustButton(CallbackQuery call, string[] args, bool untrust)
        {
            if (args.Length < 3 || !long.TryParse(args[2], out var userId)) return;
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            Bot.Api.AnswerCallbackQuery(call.Id, Commands.ChangeTrust(chatId, userId, call.From.Id, untrust, lang), showAlert: true);
        }

        /// <summary>Undo or Keep under a change Detection made: dlearn:{chat}:{adjustment}:{sent}:{undo|keep}.</summary>
        [Callback(Trigger = TrustLearning.Trigger, GroupAdminOnly = true)]
        public static void LearnButton(CallbackQuery call, string[] args)
        {
            if (args.Length < 5 || !long.TryParse(args[2], out var id) || !long.TryParse(args[3], out var sent) ||
                args[4] is not ("undo" or "keep")) return;
            TrustLearning.PressAsync(call, long.Parse(args[1]), id, sent, args[4]).GetAwaiter().GetResult();
        }

        /// <summary>Restore or Keep new under the question about a member set back to new: dtrust:{chat}:{user}:{sent}:{restore|keepnew}.</summary>
        [Callback(Trigger = TrustLearning.RestoreTrigger, GroupAdminOnly = true)]
        public static void RestoreTrustButton(CallbackQuery call, string[] args)
        {
            if (args.Length < 5 || !long.TryParse(args[2], out var userId) || !long.TryParse(args[3], out var sent) ||
                args[4] is not ("restore" or "keepnew")) return;
            TrustLearning.PressRestoreAsync(call, long.Parse(args[1]), userId, sent, args[4]).GetAwaiter().GetResult();
        }
    }
}
