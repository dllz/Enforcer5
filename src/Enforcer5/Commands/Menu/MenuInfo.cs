using System;
using System.Linq;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace Enforcer5
{
    /// <summary>
    /// What a setting does, shown when an admin taps its name in a two-column menu (name, value): a popup with
    /// <c>menuInfo_{key}</c>, changing nothing. Only the value next to it changes the setting. Until 2026-11-03 the
    /// name did what the value did, so an admin tapping a name to find out what it meant switched it. Telegram
    /// reports no long press on inline buttons, so a tap it is.
    /// </summary>
    internal static class MenuInfo
    {
        internal const string Trigger = "menuinfo";
        internal const string Prefix = "menuInfo_";

        /// <summary>The most a popup (answerCallbackQuery with show_alert) shows.</summary>
        internal const int MaxLength = 200;

        /// <summary>
        /// A setting's name that explains its row with <c>menuInfo_{key}</c>. <paramref name="arg"/> tells rows that
        /// share an explanation apart (a media type), so the popup can find the name that was tapped.
        /// </summary>
        internal static InlineButton Label(string text, long chatId, string key, string arg = null) =>
            new(text, arg == null ? $"{Trigger}:{chatId}:{key}" : $"{Trigger}:{chatId}:{key}:{arg}");

        /// <summary>
        /// The popup: the name as the menu showed it, then what it does. Null for button data that names no
        /// explanation (made up, or from an old menu).
        /// </summary>
        internal static string Text(XDocument lang, string key, string label)
        {
            if (string.IsNullOrEmpty(key) || !key.All(c => char.IsLetterOrDigit(c) || c == '_')) return null;
            var full = Prefix + key;
            if (!Has(lang, full) && !Has(English, full)) return null;
            var body = Methods.GetLocaleString(lang, full);
            var text = string.IsNullOrWhiteSpace(label) ? body : $"{label}: {body}";
            return text.Length > MaxLength ? text[..(MaxLength - 1)] + "…" : text;
        }

        /// <summary>The text of the button with <paramref name="data"/> on <paramref name="menu"/>; null when it is not there.</summary>
        internal static string LabelOf(Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup menu, string data) =>
            menu?.InlineKeyboard.SelectMany(r => r).FirstOrDefault(b => b.CallbackData == data)?.Text;

        private static XDocument English => Program.LangaugeList.FirstOrDefault(l => l.Name == "English")?.Doc;

        private static bool Has(XDocument lang, string key) =>
            lang?.Descendants("string").Any(s => (string)s.Attribute("key") == key) == true;
    }

    public static partial class CallBacks
    {
        /// <summary>A setting's name tapped: what it does, as a popup. Anyone may read it; nothing changes.</summary>
        [Callback(Trigger = MenuInfo.Trigger)]
        public static void MenuInfoPressed(CallbackQuery call, string[] args)
        {
            if (args.Length < 3 || !long.TryParse(args[1], out var chatId)) return;
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var text = MenuInfo.Text(lang, args[2], MenuInfo.LabelOf(call.Message?.ReplyMarkup, call.Data));
            Bot.Api.AnswerCallbackQuery(call.Id, text ?? Methods.GetLocaleString(lang, "doNothing"), showAlert: text != null);
        }
    }
}
