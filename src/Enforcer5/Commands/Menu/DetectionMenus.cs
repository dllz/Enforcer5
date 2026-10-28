using System;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Data;
using Enforcer5.Models;
using Enforcer5.Storage;
using Telegram.Bot.Types.Enums;
using Enforcer5.Detection;
using Enforcer5.Helpers;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5
{
    public static partial class CallBacks
    {
        /// <summary>
        /// A settings menu button that changes the group's detection settings: saves the change in
        /// PostgreSQL, redraws <paramref name="menu"/> and answers the button. When the change cannot
        /// be saved (PostgreSQL not ready, a failed write) the admin is told it failed and the menu
        /// stays as it was.
        /// </summary>
        internal static void ChangeDetection(CallbackQuery call, long chatId, XDocument lang,
            Func<DetectionPolicy, DetectionPolicy> change, Func<long, XDocument, InlineKeyboardMarkup> menu)
        {
            if (DetectionSettings.TryUpdate(chatId, change) == null)
            {
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "failed"));
                return;
            }
            var keys = menu(chatId, lang);
            Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
            Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
        }

        /// <summary>The next action of a menu's cycle; one not in it starts the cycle.</summary>
        internal static string Next(string current, params string[] cycle)
        {
            var at = Array.IndexOf(cycle, current);
            return cycle[(at + 1) % cycle.Length];
        }
    
        [Callback(Trigger = "detectionmenu", GroupAdminOnly = true)]
        public static void DetectionMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            ShowDetectionMenu(call, chatId, lang);
        }

        private static void ShowDetectionMenu(CallbackQuery call, long chatId, XDocument lang)
        {
            Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, Commands.DetectionMenuText(chatId, lang), ParseMode.Html,
                replyMarkup: Commands.GenDetectionMenu(chatId, lang)).GetAwaiter().GetResult();
        }

        /// <summary>Go live now, watch 7 more days, or watch only again for 14 days.</summary>
        [Callback(Trigger = "detectionmode", GroupAdminOnly = true)]
        public static void DetectionMode(CallbackQuery call, string[] args)
        {
            if (args.Length < 3) return;
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var now = new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero);
            Func<DetectionPolicy, DetectionPolicy> change = args[2] switch
            {
                "live" => p => p with { Engine = p.Engine with { ObserveUntil = now } },
                "extend" => p => p with
                {
                    Engine = p.Engine with { ObserveUntil = (p.Engine.ObserveUntil is { } until && until > now ? until : now).AddDays(7) }
                },
                "observe" => p => p with { Engine = p.Engine with { ObserveUntil = now + EngineSettings.ObserveFor } },
                _ => null
            };
            if (change == null) return;
            if (DetectionSettings.TryUpdate(chatId, change) == null)
            {
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "failed"));
                return;
            }
            Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            ShowDetectionMenu(call, chatId, lang);
        }

        [Callback(Trigger = "detectionretention", GroupAdminOnly = true)]
        public static void DetectionRetention(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            if (DetectionSettings.TryUpdate(chatId, p => p with
                {
                    Engine = p.Engine with { RetentionDays = NextRetention(p.Engine.RetentionDays) }
                }) == null)
            {
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "failed"));
                return;
            }
            Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            ShowDetectionMenu(call, chatId, lang);
        }

        internal static int NextRetention(int days)
        {
            var choices = EngineSettings.RetentionChoices;
            var at = Array.IndexOf(choices, days);
            return choices[(at + 1) % choices.Length];
        }
}
}

namespace Enforcer5
{
    public static partial class Commands
    {
        /// <summary>
        /// The Detection screen (design sections 1 and 12, build step 6): whether the group is still only
        /// watching and what that found, the way to go live, the filters, and how long data is kept.
        /// </summary>
        internal static string DetectionMenuText(long chatId, XDocument lang)
        {
            var engine = DetectionSettings.For(chatId).Engine;
            var now = new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero);
            string status;
            if (engine.Observing(now))
            {
                long members = 0, actions = 0;
                if (PostgresStorage.Ready)
                {
                    try
                    {
                        (members, actions) = Repositories.DetectionLog.ObservedAsync(chatId).GetAwaiter().GetResult();
                    }
                    catch (Exception e)
                    {
                        // The screen without the counts.
                        ErrorReporter.Report(e, $"Counting what detection found in {chatId}");
                    }
                }
                status = Methods.GetLocaleString(lang, "detectionObserving", Date(engine.ObserveUntil ?? now + EngineSettings.ObserveFor),
                    members, actions);
            }
            else status = Methods.GetLocaleString(lang, "detectionLive", Date(engine.ObserveUntil.Value));
            return Methods.GetLocaleString(lang, "detectionMenu") + "\n\n" + status + "\n\n" +
                   Methods.GetLocaleString(lang, "detectionRetentionText", Methods.GetLocaleString(lang, $"retention{engine.RetentionDays}"));
        }

        private static string Date(DateTimeOffset at) => at.UtcDateTime.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

        internal static InlineKeyboardMarkup GenDetectionMenu(long chatId, XDocument lang)
        {
            var engine = DetectionSettings.For(chatId).Engine;
            var mode = new Menu(2);
            if (engine.Observing(new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero)))
            {
                mode.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "detectionGoLive"), $"detectionmode:{chatId}:live")
                    { Style = KeyboardButtonStyle.Success });
                mode.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "detectionExtend"), $"detectionmode:{chatId}:extend"));
            }
            else
                mode.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "detectionWatchAgain"), $"detectionmode:{chatId}:observe"));
            var more = new Menu(1);
            var filters = DetectionSettings.For(chatId).Filters.Rules.Count(r => r.Enabled && r.Action != null);
            more.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "detectionFiltersButton", filters), $"filtersmenu:{chatId}"));
            more.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "detectionReportsButton"), $"reportsmenu:{chatId}"));
            more.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "detectionRetentionButton",
                Methods.GetLocaleString(lang, $"retention{engine.RetentionDays}")), $"detectionretention:{chatId}"));
            more.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "backButton"), $"back:{chatId}"));
            return Key.CreateMarkupFromMenus(mode, more);
        }
    }
}
