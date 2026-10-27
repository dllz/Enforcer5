using System;
using System.Xml.Linq;
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
    }
}
