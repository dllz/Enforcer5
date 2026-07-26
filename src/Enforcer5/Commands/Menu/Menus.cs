using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Args;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot;
#pragma warning disable CS4014
namespace Enforcer5
{
    public static partial class Commands
    {     
        public static InlineKeyboardMarkup genLangMenu(long chatId, XDocument lang)
        {
            var langs = Program.LangaugeList;
            List<InlineKeyboardButton> buttons = langs.Select(x => x.Name).Distinct().OrderBy(x => x).Select(x => new InlineKeyboardButton(x, $"changeLang:{chatId}:{x}")).ToList();
            buttons.Add(new InlineKeyboardButton(Methods.GetLocaleString(lang, "backButton"), $"back:{chatId}"));
            var baseMenu = new List<InlineKeyboardButton[]>();
            for (var i = 0; i < buttons.Count; i++)
            {
                if (buttons.Count - 1 == i)
                {
                    baseMenu.Add(new[] { buttons[i] });
                }
                else
                    baseMenu.Add(new[] { buttons[i], buttons[i + 1] });
                i++;
            }

            var menu = new InlineKeyboardMarkup(baseMenu.ToArray());
            return menu;
        }     
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "openLangMenu", GroupAdminOnly = true)]
        public static void openLangMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId);
            var text = Methods.GetLocaleString(lang.Doc, "langMenu", lang.Base);
            var keys = Commands.genLangMenu(chatId, lang.Doc);
             Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, text, replyMarkup: keys, parseMode:ParseMode.Html);
        }

        [Callback(Trigger = "changeLang", GroupAdminOnly = true)]
        public static void changeLang(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var newLang = args[2];
            Methods.SetGroupLang(newLang, chatId);
            var lang = Methods.GetGroupLanguage(chatId);
            var text = Methods.GetLocaleString(lang.Doc, "langMenu", lang.Base);
            var keys = Commands.genLangMenu(chatId, lang.Doc);
             Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, text, replyMarkup: keys, parseMode: ParseMode.Html);
        }       

    }
}
