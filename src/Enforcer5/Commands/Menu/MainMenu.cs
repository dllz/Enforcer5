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
        [Command(Trigger = "menu", GroupAdminOnly = true, InGroupOnly = true)]
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
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var settings = Redis.db.HashGetAllAsync($"chat:{update.Message.Chat.Id}:settings").Result;
            var mainMenu = new Menu();
            mainMenu.Columns = 2;
            mainMenu.Buttons = new List<InlineButton>();
            foreach (var mem in settings)
            {
                if (mem.Name.Equals("Flood") || mem.Name.Equals("Report") || mem.Name.Equals("Welcome"))
                {
                    if (mem.Name.Equals("Flood"))
                    {
                        mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{mem.Name}Button")));
                    }
                    else
                    {
                        mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{mem.Name}Button")));
                    }
                    if (mem.Value.Equals("yes"))
                    {
                        mainMenu.Buttons.Add(new InlineButton("🚫"));
                    }
                    else if (mem.Value.Equals("no"))
                    {
                        mainMenu.Buttons.Add(new InlineButton("✅"));
                    }
                }
                else if (mem.Name.Equals("Modlist") || mem.Name.Equals("About") || mem.Name.Equals("Rules") ||
                         mem.Name.Equals("Extra"))
                {
                    mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{mem.Name}Button")));
                    if (mem.Value.Equals("yes"))
                    {
                        mainMenu.Buttons.Add(new InlineButton("👤"));
                    }
                    else if (mem.Value.Equals("no"))
                    {
                        mainMenu.Buttons.Add(new InlineButton("👥"));
                    }
                }
            }
            settings = Redis.db.HashGetAllAsync($"chat:{update.Message.Chat.Id}:char").Result;
            foreach (var mem in settings)
            {
                mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{mem.Name}Button")));
                if (mem.Value.Equals("kick") || mem.Value.Equals("ban"))
                {
                    mainMenu.Buttons.Add(new InlineButton($"🔐 {Methods.GetLocaleString(lang, mem.Value)}"));
                }
                else if (mem.Value.Equals("allowed"))
                {
                    mainMenu.Buttons.Add(new InlineButton("✅"));
                }
            }
            var max = Redis.db.HashGetAsync($"chat:{update.Message.Chat.Id}:warnsettings", "max").Result;
            var action = Redis.db.HashGetAsync($"chat:{update.Message.Chat.Id}:warnsettings", "type").Result;
            var editWarn = new Menu(2);
            editWarn.Buttons.Add(new InlineButton($"📍 {max} 🔨 {action}"));
            var close = new Menu(1);
            close.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "closeButton"), "close"));
            var keys = Key.CreateMarkupFromMenus(mainMenu, editWarn, close);
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
            
            settings = Redis.db.HashGetAllAsync($"chat:{chatId}:char").Result;
            foreach (var mem in settings)
            {
                mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{mem.Name}Button"),
                    $"menusettings:{mem.Name}"));
                switch (mem.Value.ToString())
                {
                    case "kick":
                        mainMenu.Buttons.Add(new InlineButton($"⚡️ | {Methods.GetLocaleString(lang, "kick")}", $"menu{mem.Name}:{chatId}"));
                        break;
                    case "ban":
                        mainMenu.Buttons.Add(new InlineButton($"⛔ | {Methods.GetLocaleString(lang, "ban")}", $"menu{mem.Name}:{chatId}"));
                        break;
                    case "allowed":
                        mainMenu.Buttons.Add(new InlineButton("✅", $"menu{mem.Name}:{chatId}"));
                        break;
                    case "tempban":
                        mainMenu.Buttons.Add(new InlineButton($"⏳ | {Methods.GetLocaleString(lang, "tempban")}", $"menu{mem.Name}:{chatId}"));
                        break;

                }
            }
            var max = Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "max").Result;
            var action = Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "type").Result;
            var warnTitle = new Menu(1);

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

       

        [Callback(Trigger = "menuRtl")]
        public static void MenuRtl(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "Rtl";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:char", option).Result;
            if (current.Equals("ban"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:char", option, "kick");
                var keys = Commands.genMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:char", option, "allowed");
                var keys = Commands.genMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("allowed"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:char", option, "tempban");
                var keys = Commands.genMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("tempban"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:char", option, "ban");
                var keys = Commands.genMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "menuArab")]
        public static void MenuArab(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "Arab";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:char", option).Result;
            if (current.Equals("ban"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:char", option, "kick");
                var keys = Commands.genMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:char", option, "allowed");
                var keys = Commands.genMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("allowed"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:char", option, "tempban");
                var keys = Commands.genMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("tempban"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:char", option, "ban");
                var keys = Commands.genMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

       
    }
}
