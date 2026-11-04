using System;
using System.Collections.Generic;
using System.Linq;
using Enforcer5.Helpers;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5
{
    /// <summary>
    /// The log channel entry for an admin's button press, read off the menu itself: the pressed button's row
    /// before the press (<c>call.Message</c>) and after it (the redraw, <see cref="MenuCapture"/>). A row whose
    /// value changed logs "Beck (id) changed Late edits in Group (id): On → Off"; a button that only opens a
    /// screen or goes back logs nothing; any other press logs "Beck (id) pressed “Go live now” in Group (id)".
    /// Every menu, old and new, gets it without writing its own entry. Replaces the raw button data with
    /// "while replying to noone" (2026-10-29).
    /// </summary>
    internal static class MenuChangeLog
    {
        /// <summary>Buttons that only open a screen or go back.</summary>
        internal static readonly HashSet<string> Navigation = new(StringComparer.OrdinalIgnoreCase)
        {
            "detectionmenu", "editsmenu", "learnmenu", "filtersmenu", "reportsmenu", "floodSettings", "openLangMenu", "back"
        };

        internal enum Kind { Unknown, Changed, Pressed }

        internal sealed record Result(Kind Kind, string Label = null, string From = null, string To = null);

        /// <summary>
        /// What a press did, from the keyboard before and after it; pure, for tests. The same layout with one
        /// button's text changed, in the pressed button's row, is a setting changed: named by the row's first
        /// button when that is a label (["Late edits"]["On"]), else by the screen's title ([➖][5][➕] under
        /// "Flood"). Anything else is a press of the button.
        /// </summary>
        internal static Result Read(InlineKeyboardMarkup before, InlineKeyboardMarkup after, string data, string title)
        {
            var rows = Rows(before);
            var row = rows.FindIndex(r => r.Any(b => b.CallbackData == data));
            if (row < 0) return new Result(Kind.Unknown);
            var pressed = new Result(Kind.Pressed, rows[row].First(b => b.CallbackData == data).Text);
            var now = Rows(after);
            List<List<InlineKeyboardButton>> was, @is;
            int at;
            if (now.Count == rows.Count && Enumerable.Range(0, rows.Count).All(i => rows[i].Count == now[i].Count))
                (was, @is, at) = (rows, now, row);
            else
            {
                // The change showed or hid other rows (late edits off hides their options): the pressed row alone,
                // found by its label.
                var same = now.FindIndex(r => r.Count == rows[row].Count && r[0].Text == rows[row][0].Text);
                if (same < 0) return pressed;
                (was, @is, at) = (new() { rows[row] }, new() { now[same] }, 0);
            }

            var changed = (from r in Enumerable.Range(0, was.Count)
                           from c in Enumerable.Range(0, was[r].Count)
                           where was[r][c].Text != @is[r][c].Text
                           select (r, c)).ToList();
            if (changed.Count != 1 || changed[0].r != at) return pressed;
            var (_, column) = changed[0];
            var first = rows[row][0].Text;
            var label = column != 0 && first.Any(char.IsLetter) ? first
                : !string.IsNullOrWhiteSpace(title) ? title
                : pressed.Label;
            return new Result(Kind.Changed, label, was[at][column].Text, @is[at][column].Text);
        }

        private static List<List<InlineKeyboardButton>> Rows(InlineKeyboardMarkup markup) =>
            markup?.InlineKeyboard.Select(r => r.ToList()).ToList() ?? new List<List<InlineKeyboardButton>>();

        /// <summary>The first line of a menu's text, short: its title.</summary>
        internal static string Title(string text)
        {
            var line = (text ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
            return line.Length > 60 ? line[..60] + "…" : line;
        }

        /// <summary>
        /// Logs the press if it can be read off the menu; false when it cannot (no keyboard, or the button is not
        /// on it), so the caller logs it the old way.
        /// </summary>
        internal static bool Log(long chatId, User admin, string data, InlineKeyboardMarkup shown, string shownText,
            InlineKeyboardMarkup redrawn)
        {
            var trigger = data.Split(':')[0];
            if (Navigation.Contains(trigger)) return true;
            var result = Read(shown, redrawn, data, Title(shownText));
            if (result.Kind == Kind.Unknown) return false;
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var name = Methods.FormatHTML(admin.FirstName);
            var title = Service.GetCachedGroupTitle(chatId);
            var group = string.IsNullOrEmpty(title) ? Ids.Mark(chatId) : $"{Methods.FormatHTML(title)} ({Ids.Mark(chatId)})";
            Service.SendToLogChannel(chatId, lang, () => result.Kind == Kind.Changed
                ? Methods.GetLocaleString(lang, "logSettingChanged", name, Ids.Mark(admin.Id), Methods.FormatHTML(result.Label), group,
                    Methods.FormatHTML(result.From), Methods.FormatHTML(result.To))
                : Methods.GetLocaleString(lang, "logButtonPressed", name, Ids.Mark(admin.Id), Methods.FormatHTML(result.Label), group));
            return true;
        }
    }
}
