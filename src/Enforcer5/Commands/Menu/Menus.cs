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
#pragma warning disable CS4014
namespace Enforcer5
{
    public static partial class Commands
    {     
        public static InlineKeyboardMarkup genNSFWMenu(long chatId, XDocument lang)
        {
            var mediaList = Redis.db.HashGetAllAsync($"chat:{chatId}:nsfwDetection").Result;
            var menu = new Menu(2);
            foreach (var mem in mediaList)
            {
                if (mem.Value.Equals("on"))
                {
                    menu.Buttons.Add(new InlineButton($"✅ | {Methods.GetLocaleString(lang, "on")}",
                        $"nsfwsettings:{chatId}"));
                }
                else if (mem.Value.Equals("off"))
                {
                    menu.Buttons.Add(new InlineButton($"❌ | {Methods.GetLocaleString(lang, "off")}",
                        $"nsfwsettings:{chatId}"));
                }
                if (mem.Value.Equals("kick") || mem.Value.Equals("ban"))
                {
                    menu.Buttons.Add(new InlineButton($"🔐 {Methods.GetLocaleString(lang, mem.Value)}",
                        $"nsfw{mem.Name}:{chatId}"));
                }
            }
            var close = new Menu(1);
            close.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "backButton"), $"back:{chatId}"));
            return Key.CreateMarkupFromMenus(menu, close);
        }

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
             Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, text, replyMarkup: keys, parseMode:ParseMode.Html);
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
             Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, text, replyMarkup: keys, parseMode: ParseMode.Html);
        }       

        [Callback(Trigger = "opennsfwmenu")]
        public static void openNSFWMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var text = Methods.GetLocaleString(lang, "nsfwMenu");
            var keys = Commands.genNSFWMenu(chatId, lang);
             Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, text, replyMarkup: keys, parseMode: ParseMode.Html);
        }
        [Callback(Trigger = "nsfwaction", GroupAdminOnly = true)]
        public static void nsfwAction(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "action";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:nsfwDetection", option).Result;
            if (current.Equals("ban"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", option, "kick");
                var keys = Commands.genNSFWMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", option, "ban");
                var keys = Commands.genNSFWMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "nsfwsettings", GroupAdminOnly = true)]
        public static void nsfwsettings(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "activated";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:nsfwDetection", option).Result;
            if (current.Equals("on"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", option, "off");
                var keys = Commands.genNSFWMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("off"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:nsfwDetection", option, "on");
                var keys = Commands.genNSFWMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }
    }
}
