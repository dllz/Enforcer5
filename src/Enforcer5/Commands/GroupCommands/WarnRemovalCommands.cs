using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5
{
    /// <summary>
    /// Removing and resetting warns, with an optional reason.
    ///
    /// Every way of taking warns off goes through <see cref="Request"/>: the buttons under a /warn
    /// or /prewarn message, the /user and /media menus, and /removewarn and /resetwarns. When the
    /// group's <see cref="WarnReasonMode"/> covers the kind of warn and no reason was given, nothing
    /// changes yet: the bot asks, offering saved and recent reasons as buttons, and applies the
    /// change once the admin who asked taps one or replies with their own. Either way the result is
    /// shown in the group and written to the log channel.
    /// </summary>
    internal static class WarnRemovals
    {
        /// <summary>How long a question waits for its reason before it lapses and nothing changes.</summary>
        internal static readonly TimeSpan PromptLifetime = TimeSpan.FromMinutes(10);

        internal const string ReasonCallback = "warnreason";
        private const string CancelChoice = "cancel";

        /// <summary>Entry point for the removal buttons, whose data is <c>trigger:chatId:userId</c>.</summary>
        internal static void FromButton(CallbackQuery call, string[] args, WarnKind kind, bool reset, bool alsoMediaWarns = false)
        {
            if (args.Length < 3 || !long.TryParse(args[2], out var targetId)) return;
            var change = new WarnChange(kind, reset, targetId, alsoMediaWarns);
            Request(call.Message.Chat.Id, change, call.From.Id, call.From.FirstName, null,
                call.Message.MessageId, sourceIsBotMessage: true, callbackId: call.Id);
        }

        /// <summary>
        /// Removes or resets now if a reason is given or none is required; otherwise asks for one.
        /// </summary>
        internal static void Request(long chatId, WarnChange change, long adminId, string adminName, string reason,
            int sourceMessageId, bool sourceIsBotMessage, string callbackId = null)
        {
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var target = Service.GetCachedUserLabel(change.TargetId);

            var current = CountAsync(Repositories.Warns, chatId, change).GetAwaiter().GetResult();
            if (current == 0)
            {
                var nothing = Methods.GetLocaleString(lang, "warnNothingToRemove", target,
                    KindLabel(lang, change.Kind));
                if (callbackId != null) Bot.Api.AnswerCallbackQuery(callbackId, nothing, true);
                else Bot.SendReply(nothing, chatId, sourceMessageId);
                return;
            }

            var mode = Repositories.WarnReasons.GetModeAsync(chatId).GetAwaiter().GetResult();
            if (reason == null && mode.Requires(change.Kind))
            {
                Ask(chatId, lang, change, adminId, adminName, target, current, sourceMessageId, sourceIsBotMessage);
            }
            else
            {
                Complete(chatId, lang, change, adminId, adminName, target, reason, sourceMessageId, sourceIsBotMessage);
            }
            if (callbackId != null) Bot.Api.AnswerCallbackQuery(callbackId);
        }

        /// <summary>A reason button under a question, or its Cancel button.</summary>
        internal static void AnswerFromButton(CallbackQuery call, string[] args)
        {
            var chatId = call.Message.Chat.Id;
            var promptId = call.Message.MessageId;
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var repository = Repositories.WarnReasons;

            var pending = repository.GetPendingAsync(chatId, promptId).GetAwaiter().GetResult();
            if (pending == null)
            {
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "warnReasonExpired"), true);
                TryDelete(chatId, promptId);
                return;
            }

            // AllowedIfAdmin needs no check here: GroupAdminOnly callbacks only reach admins.
            if (WarnReasons.WhoMayAnswer(pending.AdminId, call.From.Id, false) == Answerer.Refused)
            {
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "warnReasonNotYours", pending.AdminName), true);
                return;
            }

            string reason = null;
            var choice = args.Length > 2 ? args[2] : null;
            if (choice != CancelChoice)
            {
                if (!int.TryParse(choice, out var index) || index < 0 || index >= (pending.Reasons?.Length ?? 0)) return;
                reason = pending.Reasons[index];
            }

            if (!repository.ClaimPendingAsync(chatId, promptId).GetAwaiter().GetResult())
            {
                // Someone else answered in the meantime.
                Bot.Api.AnswerCallbackQuery(call.Id);
                return;
            }
            TryDelete(chatId, promptId);

            if (reason == null)
            {
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "warnReasonCancelled"));
                return;
            }
            Bot.Api.AnswerCallbackQuery(call.Id);
            Complete(chatId, lang, pending.Change, call.From.Id, call.From.FirstName,
                Service.GetCachedUserLabel(pending.Change.TargetId), reason, pending.SourceMessageId, pending.SourceIsBotMessage);
        }

        /// <summary>
        /// Whether <paramref name="message"/> could be a typed reason: text replying to the bot, and
        /// not a command. Cheap, so the update path can test every message without a Redis read.
        /// </summary>
        internal static bool MayBeReasonReply(Message message, long botId) =>
            message.ReplyToMessage?.From?.Id == botId &&
            !string.IsNullOrEmpty(message.Text) &&
            !message.Text.StartsWith("/");

        /// <summary>A typed reason, as a reply to the question. Ignored unless it answers one.</summary>
        internal static void AnswerFromReply(Message message)
        {
            var chatId = message.Chat.Id;
            var promptId = message.ReplyToMessage.MessageId;
            var repository = Repositories.WarnReasons;

            var pending = repository.GetPendingAsync(chatId, promptId).GetAwaiter().GetResult();
            if (pending == null) return; // an ordinary reply to some other bot message

            var anonymous = message.SenderChat != null && message.SenderChat.Id == chatId;
            switch (WarnReasons.WhoMayAnswer(pending.AdminId, message.From.Id, anonymous))
            {
                case Answerer.Refused:
                    return;
                case Answerer.AllowedIfAdmin:
                    if (!Methods.IsGroupAdmin(message.From.Id, chatId)) return;
                    break;
            }

            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var reason = WarnReasons.Normalise(message.Text);
            if (reason == null)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "warnReasonTooLong", WarnReasons.MaxLength), message);
                return;
            }

            if (!repository.ClaimPendingAsync(chatId, promptId).GetAwaiter().GetResult()) return;
            TryDelete(chatId, promptId);
            TryDelete(chatId, message.MessageId);

            // An anonymous answer cannot be attributed, so it is credited to whoever asked.
            var adminId = anonymous ? pending.AdminId : message.From.Id;
            var adminName = anonymous ? pending.AdminName : message.From.FirstName;
            Complete(chatId, lang, pending.Change, adminId, adminName,
                Service.GetCachedUserLabel(pending.Change.TargetId), reason, pending.SourceMessageId, pending.SourceIsBotMessage);
        }

        private static void Ask(long chatId, XDocument lang, WarnChange change, long adminId, string adminName,
            string target, long current, int sourceMessageId, bool sourceIsBotMessage)
        {
            var repository = Repositories.WarnReasons;
            var saved = SavedOrDefaultsAsync(repository, chatId, lang).GetAwaiter().GetResult();
            var recent = repository.GetRecentAsync(chatId).GetAwaiter().GetResult();
            var choices = WarnReasons.Choices(saved, recent);

            var menu = new Menu(1, choices
                .Select((reason, index) => new InlineButton(reason, $"{ReasonCallback}:{chatId}:{index}"))
                .ToList());
            menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "warnReasonCancel"),
                $"{ReasonCallback}:{chatId}:{CancelChoice}"));

            var prompt = Bot.SendReply(PromptText(lang, change, adminName, target, current), chatId, sourceMessageId,
                Key.CreateMarkupFromMenu(menu));
            if (prompt == null) return; // could not ask, so nothing changes

            repository.SavePendingAsync(chatId, prompt.MessageId,
                new PendingWarnRemoval(change, adminId, adminName, sourceMessageId, sourceIsBotMessage, choices),
                PromptLifetime).GetAwaiter().GetResult();
        }

        private static void Complete(long chatId, XDocument lang, WarnChange change, long adminId, string adminName,
            string target, string reason, int sourceMessageId, bool sourceIsBotMessage)
        {
            var left = ApplyAsync(Repositories.Warns, Repositories.WarnReasons, chatId, change, reason).GetAwaiter().GetResult();

            var text = ResultText(lang, change, adminName, target, left, reason);
            var shown = false;
            if (sourceIsBotMessage)
            {
                try
                {
                    // Replaces the message the button was on, as removals always have; this also
                    // removes its buttons.
                    Bot.Api.EditMessageText(chatId, sourceMessageId, text, parseMode: ParseMode.Html).Wait();
                    shown = true;
                }
                catch (Exception e)
                {
                    LogHelper.Error($"Editing warn message {sourceMessageId} in {chatId} failed: {Bot.AsApiError(e)?.Message ?? e.Message}");
                }
            }
            if (!shown) Bot.SendReply(text, chatId, sourceMessageId);

            var group = Service.GetCachedGroupTitle(chatId);
            Service.SendToLogChannel(chatId, lang,
                () => LogText(lang, change, adminName, adminId, target, group, chatId, left, reason));
        }

        private static void TryDelete(long chatId, int messageId)
        {
            try
            {
                Bot.DeleteMessage(chatId, messageId);
            }
            catch (Exception e)
            {
                // Without the delete right the question and reply just stay in the chat.
                LogHelper.Error($"Could not delete message {messageId} in {chatId}: {Bot.AsApiError(e)?.Message ?? e.Message}");
            }
        }

        // ------------------------------------------------------------------------------------
        // The parts below touch neither Telegram nor the static Redis connection, so they are
        // unit tested.
        // ------------------------------------------------------------------------------------

        /// <summary>The warns this change would take off; 0 means there is nothing to do.</summary>
        internal static async Task<long> CountAsync(IWarnRepository warns, long chatId, WarnChange change)
        {
            var count = await warns.GetCountAsync(chatId, change.Kind, change.TargetId);
            if (change.Reset && change.AlsoMediaWarns && change.Kind != WarnKind.Media)
                count += await warns.GetCountAsync(chatId, WarnKind.Media, change.TargetId);
            return count;
        }

        /// <summary>Makes the change and remembers the reason. Returns the count left.</summary>
        internal static async Task<long> ApplyAsync(IWarnRepository warns, IWarnReasonRepository reasons,
            long chatId, WarnChange change, string reason)
        {
            long left = 0;
            if (change.Reset)
            {
                await warns.ResetAsync(chatId, change.Kind, change.TargetId);
                if (change.AlsoMediaWarns && change.Kind != WarnKind.Media)
                    await warns.ResetAsync(chatId, WarnKind.Media, change.TargetId);
            }
            else
            {
                left = await warns.RemoveOneAsync(chatId, change.Kind, change.TargetId);
            }

            if (reason != null) await reasons.AddRecentAsync(chatId, reason);
            return left;
        }

        /// <summary>The group's saved reasons, or the language's defaults if it never edited them.</summary>
        internal static async Task<IReadOnlyList<string>> SavedOrDefaultsAsync(IWarnReasonRepository reasons, long chatId, XDocument lang) =>
            await reasons.GetSavedAsync(chatId) ?? WarnReasons.ParseDefaults(Methods.GetLocaleString(lang, "warnReasonDefaults"));

        internal static string KindLabel(XDocument lang, WarnKind kind) => Methods.GetLocaleString(lang, kind switch
        {
            WarnKind.Prewarn => "warnKindPrewarn",
            WarnKind.Media => "warnKindMedia",
            _ => "warnKindWarn"
        });

        internal static string PromptText(XDocument lang, WarnChange change, string adminName, string target, long current) =>
            string.Join("\n",
                Methods.GetLocaleString(lang, change.Reset ? "warnReasonPromptReset" : "warnReasonPromptRemove", adminName, target),
                Methods.GetLocaleString(lang, "warnCountLine", KindLabel(lang, change.Kind), current),
                "",
                Methods.GetLocaleString(lang, "warnReasonPromptHint"));

        /// <summary>What the group sees once the change is made.</summary>
        internal static string ResultText(XDocument lang, WarnChange change, string adminName, string target, long left, string reason)
        {
            var lines = new List<string>
            {
                Methods.GetLocaleString(lang, change.Reset ? "warnChangeReset" : "warnChangeRemoved", adminName, target),
                Methods.GetLocaleString(lang, "warnCountLine", KindLabel(lang, change.Kind), left)
            };
            if (reason != null) lines.Add(Methods.GetLocaleString(lang, "warnChangeReason", reason));
            return string.Join("\n", lines);
        }

        /// <summary>The log channel entry.</summary>
        internal static string LogText(XDocument lang, WarnChange change, string adminName, long adminId, string target,
            string group, long chatId, long left, string reason)
        {
            var lines = new List<string>
            {
                Methods.GetLocaleString(lang, change.Reset ? "logWarnReset" : "logWarnRemoved",
                    adminName, adminId, target, string.IsNullOrEmpty(group) ? $"{chatId}" : $"{group} ({chatId})"),
                Methods.GetLocaleString(lang, "warnCountLine", KindLabel(lang, change.Kind), left),
                reason != null
                    ? Methods.GetLocaleString(lang, "warnChangeReason", reason)
                    : Methods.GetLocaleString(lang, "warnChangeNoReason")
            };
            return string.Join("\n", lines);
        }
    }

    public static partial class Commands
    {
        [Command(Trigger = "removewarn", InGroupOnly = true, GroupAdminOnly = true, RequiresReply = true)]
        public static void RemoveWarnCommand(Update update, string[] args) => WarnCommand(update, args, reset: false);

        [Command(Trigger = "resetwarns", InGroupOnly = true, GroupAdminOnly = true, RequiresReply = true)]
        public static void ResetWarnsCommand(Update update, string[] args) => WarnCommand(update, args, reset: true);

        private static void WarnCommand(Update update, string[] args, bool reset)
        {
            var message = update.Message;
            var lang = Methods.GetGroupLanguage(message, true).Doc;
            var target = message.ReplyToMessage.From;
            if (target == null || target.Id == Bot.Me.Id) return;

            string reason = null;
            if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1]))
            {
                reason = WarnReasons.Normalise(args[1]);
                if (reason == null)
                {
                    Bot.SendReply(Methods.GetLocaleString(lang, "warnReasonTooLong", WarnReasons.MaxLength), update);
                    return;
                }
            }

            WarnRemovals.Request(message.Chat.Id, new WarnChange(WarnKind.Warn, reset, target.Id),
                message.From.Id, message.From.FirstName, reason, message.MessageId, sourceIsBotMessage: false);
        }

        [Command(Trigger = "addwarnreason", InGroupOnly = true, GroupAdminOnly = true)]
        public static void AddWarnReason(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var chatId = update.Message.Chat.Id;
            var input = args.Length > 1 ? args[1] : null;
            if (string.IsNullOrWhiteSpace(input))
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "warnReasonUsage", WarnReasons.MaxLength), update);
                return;
            }
            var reason = WarnReasons.Normalise(input);
            if (reason == null)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "warnReasonTooLong", WarnReasons.MaxLength), update);
                return;
            }

            var repository = Repositories.WarnReasons;
            var reasons = WarnRemovals.SavedOrDefaultsAsync(repository, chatId, lang).GetAwaiter().GetResult().ToList();
            if (reasons.Any(r => string.Equals(r, reason, StringComparison.OrdinalIgnoreCase)))
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "warnReasonExists", reason), update);
                return;
            }
            if (reasons.Count >= WarnReasons.MaxSaved)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "warnReasonLimit", WarnReasons.MaxSaved), update);
                return;
            }

            reasons.Add(reason);
            repository.SetSavedAsync(chatId, reasons).GetAwaiter().GetResult();
            Bot.SendReply(Methods.GetLocaleString(lang, "warnReasonAdded", reason), update);
            Service.LogCommand(update, update.Message.Text);
        }

        [Command(Trigger = "delwarnreason", InGroupOnly = true, GroupAdminOnly = true)]
        public static void DeleteWarnReason(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var chatId = update.Message.Chat.Id;
            var input = WarnReasons.Normalise(args.Length > 1 ? args[1] : null);
            if (input == null)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "warnReasonDelUsage"), update);
                return;
            }

            var repository = Repositories.WarnReasons;
            var reasons = WarnRemovals.SavedOrDefaultsAsync(repository, chatId, lang).GetAwaiter().GetResult().ToList();
            var index = FindWarnReason(reasons, input);
            if (index < 0)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "warnReasonNotFound", input), update);
                return;
            }

            var removed = reasons[index];
            reasons.RemoveAt(index);
            repository.SetSavedAsync(chatId, reasons).GetAwaiter().GetResult();
            Bot.SendReply(Methods.GetLocaleString(lang, "warnReasonRemoved", removed), update);
            Service.LogCommand(update, update.Message.Text);
        }

        [Command(Trigger = "warnreasons", InGroupOnly = true, GroupAdminOnly = true)]
        public static void WarnReasonList(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var chatId = update.Message.Chat.Id;
            var repository = Repositories.WarnReasons;
            var saved = repository.GetSavedAsync(chatId).GetAwaiter().GetResult();
            var reasons = saved ?? WarnReasons.ParseDefaults(Methods.GetLocaleString(lang, "warnReasonDefaults"));
            if (reasons.Count == 0)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "warnReasonListEmpty"), update);
                return;
            }

            var list = string.Join("\n", reasons.Select((reason, i) => $"{i + 1}. {reason}"));
            var text = Methods.GetLocaleString(lang, "warnReasonList", list);
            if (saved == null) text += "\n\n" + Methods.GetLocaleString(lang, "warnReasonListDefaults");
            Bot.SendReply(text, update);
        }

        /// <summary>
        /// The position of <paramref name="input"/> in <paramref name="reasons"/>: its text, ignoring
        /// case, or its 1-based number as /warnreasons shows it. -1 if neither.
        /// </summary>
        internal static int FindWarnReason(IReadOnlyList<string> reasons, string input)
        {
            for (var i = 0; i < reasons.Count; i++)
            {
                if (string.Equals(reasons[i], input, StringComparison.OrdinalIgnoreCase)) return i;
            }
            return int.TryParse(input, out var number) && number >= 1 && number <= reasons.Count ? number - 1 : -1;
        }
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = WarnRemovals.ReasonCallback, GroupAdminOnly = true)]
        public static void WarnReason(CallbackQuery call, string[] args) => WarnRemovals.AnswerFromButton(call, args);

        [Callback(Trigger = "menuWarnReasons", GroupAdminOnly = true)]
        public static void MenuWarnReasons(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var repository = Repositories.WarnReasons;
            var mode = repository.GetModeAsync(chatId).GetAwaiter().GetResult();
            repository.SetModeAsync(chatId, mode.Next()).GetAwaiter().GetResult();
            RedrawSettingsMenu(call, chatId, lang);
        }
    }
}
