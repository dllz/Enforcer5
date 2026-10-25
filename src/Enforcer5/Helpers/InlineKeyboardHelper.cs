using System;
using System.Linq;
using Telegram.Bot.Types.ReplyMarkups;
using System.Collections.Generic;
using Telegram.Bot.Types;
using Enforcer5.Models;
namespace Enforcer5.Helpers
{
    public static class Key
    {
        public static InlineKeyboardMarkup CreateMarkupFromMenu(Menu menu)
        {
            if (menu == null) return null;
            var buttons = Present(menu);
            var col = menu.Columns - 1;
            //this is gonna be fun...
            var final = new List<InlineKeyboardButton[]>();
            for (var i = 0; i < buttons.Count; i++)
            {
                var row = new List<InlineKeyboardButton>();
                do
                {
                    try
                    {
                        var cur = buttons[i];
                        if (!string.IsNullOrEmpty(cur.Url))
                        {
                            row.Add(new InlineKeyboardButton(cur.Text)
                            {
                                Url = cur.Url
                            });

                        }
                        else
                        {
                            row.Add(new InlineKeyboardButton(cur.Text, $"{cur.Trigger}"));
                        }
                    }
                    catch (Exception e)
                    {
                        // A broken button is a bug in the menu builder; skip it and report.
                        ErrorReporter.Report(e, $"Building menu button {i}");
                    }       
                    i++;
                    if (i == buttons.Count) break;
                } while (i % (col + 1) != 0);
                i--;
                final.Add(row.ToArray());
                if (i == buttons.Count) break;
            }
            return new InlineKeyboardMarkup(final.ToArray());
        }

        /// <summary>
        /// The menu's buttons without the null entries: menus leave a slot null for an optional
        /// button that does not apply (the report's go-to-chat link in a group with no link), so the
        /// rest close up instead of the null being reported as a broken button.
        /// </summary>
        private static List<InlineButton> Present(Menu menu) =>
            menu.Buttons?.Where(b => b != null).ToList() ?? new List<InlineButton>();

        public static InlineKeyboardMarkup CreateMarkupFromMenus(params Menu[] menus)
        {
            if (menus.Length < 1)
                return null;
            var final = new List<InlineKeyboardButton[]>();
            for (int j = 0; j < menus.Length; j++)
            {
                var buttons = Present(menus[j]);
                var col = menus[j].Columns - 1;
                for (var i = 0; i < buttons.Count; i++)
                {
                    var row = new List<InlineKeyboardButton>();
                    do
                    {
                        try
                        {
                            var cur = buttons[i];
                            if (!string.IsNullOrEmpty(cur.Url))
                            {
                                row.Add(new InlineKeyboardButton(cur.Text)
                                {
                                    Url = cur.Url
                                });
                                                            
                            }
                            else
                            {
                                row.Add(new InlineKeyboardButton(cur.Text, $"{cur.Trigger}"));
                            }
                        }
                        catch (Exception e)
                        {
                            // A broken button is a bug in the menu builder; skip it and report.
                            ErrorReporter.Report(e, $"Building menu button {i} of menu {j}");
                        }
                        i++;
                        if (i == buttons.Count) break;
                    } while (i % (col + 1) != 0);
                    i--;
                    final.Add(row.ToArray());
                    if (i == buttons.Count) break;
                }
            }
            return new InlineKeyboardMarkup(final.ToArray());
        }
    }
}