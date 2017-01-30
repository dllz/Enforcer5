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
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5   
{
    public static partial class Commands
    {
        [Command(Trigger = "menu", GroupAdminOnly = true, InGroupOnly = true)]
        public static async Task Menu(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var chatId = update.Message.Chat.Id;
            var menuText = Methods.GetLocaleString(lang, "mainMenu", update.Message.Chat.Title);
            var menu = genMenu(chatId, lang);
            await Bot.Send(menuText, update.Message.From.Id, customMenu:menu);
            await Bot.SendReply(Methods.GetLocaleString(lang, "botPm"), update);
        }
        [Command(Trigger = "dashboard", GroupAdminOnly = true, InGroupOnly = true)]
        public static async Task Dashboard(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            //var buttons = new[]
            //{
            //    new InlineKeyboardButton(), 
            //}
        }

        public static InlineKeyboardMarkup genMenu(long chatId, XDocument lang)
        {
            var settings = Redis.db.HashGetAllAsync($"chat:{chatId}:settings").Result;
            var mainMenu = new Menu();
            mainMenu.Columns = 2;
            mainMenu.Buttons = new List<InlineButton>();
            foreach (var mem in settings)
            {
                if (mem.Name.Equals("Flood") || mem.Name.Equals("Report") || mem.Name.Equals("Welcome"))
                {
                    mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{mem.Name}Button"), $"menusettings:{mem.Name}"));
                    if (mem.Value.Equals("yes"))
                    {
                        mainMenu.Buttons.Add(new InlineButton("🚫", $"menu{mem.Name}:{chatId}"));
                    }
                    else if (mem.Value.Equals("no"))
                    {
                        mainMenu.Buttons.Add(new InlineButton("✅", $"menu{mem.Name}:{chatId}"));
                    }
                }
                else if (mem.Name.Equals("Modlist") || mem.Name.Equals("About") || mem.Name.Equals("Rules") ||
                         mem.Name.Equals("Extra"))
                {
                    mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{mem.Name}Button"), $"menusettings:{mem.Name}"));
                    if (mem.Value.Equals("yes"))
                    {
                        mainMenu.Buttons.Add(new InlineButton("👤", $"menu{mem.Name}:{chatId}"));
                    }
                    else if (mem.Value.Equals("no"))
                    {
                        mainMenu.Buttons.Add(new InlineButton("👥", $"menu{mem.Name}:{chatId}"));
                    }
                }
            }
            settings = Redis.db.HashGetAllAsync($"chat:{chatId}:char").Result;
            foreach (var mem in settings)
            {
                mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{mem.Name}Button"), $"menusettings:{mem.Name}"));
                if (mem.Value.Equals("kick") || mem.Value.Equals("ban"))
                {
                    mainMenu.Buttons.Add(new InlineButton($"🔐 {Methods.GetLocaleString(lang, mem.Value)}", $"menu{mem.Name}:{chatId}"));
                }
                else if (mem.Value.Equals("allowed"))
                {
                    mainMenu.Buttons.Add(new InlineButton("✅", $"menu{mem.Name}:{chatId}"));
                }
            }
            var max = Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "max").Result;
            var action = Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "type").Result;
            var warnTitle = new Menu(1);
            warnTitle.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "WarnsButton"), "menualert:warns"));
            var editWarn = new Menu(3);
            editWarn.Buttons.Add(new InlineButton("➖", $"menuDimWarn:{chatId}"));
            editWarn.Buttons.Add(new InlineButton($"📍 {max} 🔨 {action}", $"menuActionWarn:{chatId}"));
            editWarn.Buttons.Add(new InlineButton("➕", $"menuRaiseWarn:{chatId}"));
            var close = new Menu(1);
            close.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "closeButton"), "close"));
            return Key.CreateMarkupFromMenus(mainMenu, warnTitle, editWarn, close);
        }
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "close")]
        public static async Task CloseButton(CallbackQuery call, string[] args)
        {
            await Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, "Good Bye");
        }

        [Callback(Trigger = "menusettings")]
        public static async Task MenuChanges(CallbackQuery call, string[] args)
        {
            await Bot.Api.AnswerCallbackQueryAsync(call.Id, "Still coming");
        }
        [Callback(Trigger = "menuFlood")]
        public static async Task MenuFlood(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "Flood";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:settings", option).Result;
            if (current.Equals("yes"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "no");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }
        [Callback(Trigger = "menuReport")]
        public static async Task MenuReport(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "Report";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:settings", option).Result;
            if (current.Equals("yes"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "no");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }
        [Callback(Trigger = "menuWelcome")]
        public static async Task MenuWelcome(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "Welcome";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:settings", option).Result;
            if (current.Equals("yes"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "no");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "menuModlist")]
        public static async Task MenuModlist(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "Modlist";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:settings", option).Result;
            if (current.Equals("yes"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "no");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "menuRules")]
        public static async Task MenuRules(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "Rules";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:settings", option).Result;
            if (current.Equals("yes"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "no");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "menuExtra")]
        public static async Task MenuExtra(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "Extra";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:settings", option).Result;
            if (current.Equals("yes"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "no");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "menuAbout")]
        public static async Task MenuAbout(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "About";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:settings", option).Result;
            if (current.Equals("yes"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "no");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "menuRtl")]
        public static async Task MenuRtl(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "Rtl";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:settings", option).Result;
            if (current.Equals("ban"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "kick");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "allowed");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("allowed"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "ban");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "menuArab")]
        public static async Task MenuArab(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "Arab";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:settings", option).Result;
            if (current.Equals("ban"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "kick");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "allowed");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("allowed"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "ban");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "menuDimWarn")]
        public static async Task menuDimWarn(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var num = Redis.db.HashDecrementAsync($"chat:{chatId}:warnsettings", "max").Result;
            if (num > 0)
            {
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else
            {
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "warnToLow"));
                await Redis.db.HashIncrementAsync($"chat:{chatId}:warnsettings", "max");
            }
        }

        [Callback(Trigger = "menuRaiseWarn")]
        public static async Task menuRaiseWarn(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            await Redis.db.HashIncrementAsync($"chat:{chatId}:warnsettings", "max");
            var keys = Commands.genMenu(chatId, lang);
            await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
            await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
        }

        [Callback(Trigger = "menuActionWarn")]
        public static async Task menuActionWarn(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "type";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", option).Result;
            if (current.Equals("ban"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:warnsettings", option, "kick");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                await Redis.db.HashSetAsync($"chat:{chatId}:warnsettings", option, "ban");
                var keys = Commands.genMenu(chatId, lang);
                await Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                await Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }
    }
}
