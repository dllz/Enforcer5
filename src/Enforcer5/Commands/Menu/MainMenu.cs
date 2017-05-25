using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5
{
    public static partial class Commands
    {
        [Command(Trigger = "menu", InGroupOnly = true)]
        public static void Menu(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var chatId = update.Message.Chat.Id;
            var menuText = Methods.GetLocaleString(lang, "mainMenu", update.Message.Chat.Title);
            var menu = genMenu(chatId, lang);
            Bot.Send(menuText, update.Message.From.Id, customMenu: menu);
            Bot.SendReply(Methods.GetLocaleString(lang, "botPm"), update);
            Service.LogCommand(update, update.Message.Text);
        }

        [Command(Trigger = "dashboard", InGroupOnly = true)]
        public static void Dashboard(Update update, string[] args)
        {
            var chatId = update.Message.Chat.Id;
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var mainMenu = new Menu();
            mainMenu.Columns = 2;
            mainMenu.Buttons = new List<InlineButton>();
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"FloodButton"),
                $"openFloodMenu:{chatId}"));
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "lengthButton"),
                $"openLengthMenu:{chatId}"));
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "nsfwButton"),
                $"opennsfwmenu:{chatId}"));
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "Warn"), $"openWarnMenu:{chatId}"));
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "groupSettingButton"), $"openGroupMenu:{chatId}"));
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "mediaMenuHeader"),
                $"openMediaMenu:{chatId}"));
            var close = new Menu(1);
            close.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "closeButton"), "close"));
            var keys = Key.CreateMarkupFromMenus(mainMenu, close);
            var text = Methods.GetLocaleString(lang, "dashboardMenu");
            Bot.Send(text, update.Message.From.Id, customMenu: keys);
            Bot.SendReply(Methods.GetLocaleString(lang, "botPm"), update);
        }

        public static InlineKeyboardMarkup genMenu(long chatId, XDocument lang)
        {
            var settings = Redis.db.HashGetAllAsync($"chat:{chatId}:settings").Result;
            var mainMenu = new Menu();
            mainMenu.Columns = 2;
            mainMenu.Buttons = new List<InlineButton>();
            
           
            
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"FloodButton"),
                $"openFloodMenu:{chatId}"));
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "lengthButton"),
                $"openLengthMenu:{chatId}"));
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "nsfwButton"),
                $"opennsfwmenu:{chatId}"));
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "Warn"), $"openWarnMenu:{chatId}"));
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "groupSettingButton"), $"openGroupMenu:{chatId}"));            
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "mediaMenuHeader"),
                $"openMediaMenu:{chatId}"));
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "groupLanguage"),
                $"openLangMenu:{chatId}"));
           
            var close = new Menu(1);
            close.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "closeButton"), "close"));
            return Key.CreateMarkupFromMenus(mainMenu, close);
        }

    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "close")]
        public static void CloseButton(CallbackQuery call, string[] args)
        {
            Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, "Good Bye");
        }

        [Callback(Trigger = "back")]
        public static void BackTaskButton(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var keys = Commands.genMenu(chatId, lang);
            var menuText = Methods.GetLocaleString(lang, "mainMenu", "");
            Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, replyMarkup: keys, text: menuText);
        }

        [Callback(Trigger = "menusettings")]
        public static void MenuChanges(CallbackQuery call, string[] args)
        {
            Bot.Api.AnswerCallbackQueryAsync(call.Id, "Still coming");
        }       
    }
}
