using System;
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
            var col = menu.Columns - 1;
            //this is gonna be fun...
            var final = new List<InlineKeyboardButton[]>();
            for (var i = 0; i < menu.Buttons.Count; i++)
            {
                var row = new List<InlineKeyboardButton>();
                do
                {
                    try
                    {
                        var cur = menu.Buttons[i];
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
                        //isNull
                    }       
                    i++;
                    if (i == menu.Buttons.Count) break;
                } while (i % (col + 1) != 0);
                i--;
                final.Add(row.ToArray());
                if (i == menu.Buttons.Count) break;
            }
            return new InlineKeyboardMarkup(final.ToArray());
        }
    }
}