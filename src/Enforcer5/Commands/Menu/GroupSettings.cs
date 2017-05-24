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
        public static InlineKeyboardMarkup genGroupSettingsMenu(long chatId, XDocument lang)
        {
            var settings = Redis.db.HashGetAllAsync($"chat:{chatId}:settings").Result;
            var mainMenu = new Menu();
            mainMenu.Columns = 2;
            mainMenu.Buttons = new List<InlineButton>();
            foreach (var mem in settings)
            {
                if (mem.Name.Equals("Flood") || mem.Name.Equals("Report") || mem.Name.Equals("Welcome"))
                {
                    mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{mem.Name}Button"),
                        $"menusettings:{mem.Name}"));
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
                    mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{mem.Name}Button"),
                        $"menusettings:{mem.Name}"));
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
            var close = new Menu(1);
            close.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "backButton"), $"back:{chatId}"));
            return Key.CreateMarkupFromMenus(mainMenu, close);
        }
    }

    public static partial class CallBacks
    {

        [Callback(Trigger = "openGroupMenu", GroupAdminOnly = true)]
        public static void openGroupMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var text = Methods.GetLocaleString(lang, "groupMenu");
            var keys = Commands.genGroupSettingsMenu(chatId, lang);
            Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, text, replyMarkup: keys);
        }
        [Callback(Trigger = "menuFlood")]
        public static void MenuFlood(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "Flood";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:settings", option).Result;
            if (current.Equals("yes"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "no");
                var keys = Commands.genGroupSettingsMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genGroupSettingsMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "menuReport")]
        public static void MenuReport(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "Report";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:settings", option).Result;
            if (current.Equals("yes"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "no");
                var keys = Commands.genGroupSettingsMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genGroupSettingsMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "menuWelcome")]
        public static void MenuWelcome(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "Welcome";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:settings", option).Result;
            if (current.Equals("yes"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "no");
                var keys = Commands.genGroupSettingsMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genGroupSettingsMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "menuModlist")]
        public static void MenuModlist(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "Modlist";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:settings", option).Result;
            if (current.Equals("yes"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "no");
                var keys = Commands.genGroupSettingsMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genGroupSettingsMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "menuRules")]
        public static void MenuRules(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "Rules";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:settings", option).Result;
            if (current.Equals("yes"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "no");
                var keys = Commands.genGroupSettingsMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genGroupSettingsMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "menuExtra")]
        public static void MenuExtra(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "Extra";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:settings", option).Result;
            if (current.Equals("yes"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "no");
                var keys = Commands.genGroupSettingsMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genGroupSettingsMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "menuAbout")]
        public static void MenuAbout(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "About";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:settings", option).Result;
            if (current.Equals("yes"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "no");
                var keys = Commands.genGroupSettingsMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genGroupSettingsMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }
    }
}
