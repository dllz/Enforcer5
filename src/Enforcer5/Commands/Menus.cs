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
        [Command(Trigger = "menu", GroupAdminOnly = true, InGroupOnly = true)]
        public static void Menu(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message).Doc;
            var chatId = update.Message.Chat.Id;
            var menuText = Methods.GetLocaleString(lang, "mainMenu", update.Message.Chat.Title);
            var menu = genMenu(chatId, lang);
             Bot.Send(menuText, update.Message.From.Id, customMenu: menu);
             Bot.SendReply(Methods.GetLocaleString(lang, "botPm"), update);
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
            settings = Redis.db.HashGetAllAsync($"chat:{chatId}:char").Result;
            foreach (var mem in settings)
            {
                mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{mem.Name}Button"),
                    $"menusettings:{mem.Name}"));
                if (mem.Value.Equals("kick") || mem.Value.Equals("ban"))
                {
                    mainMenu.Buttons.Add(new InlineButton($"🔐 {Methods.GetLocaleString(lang, mem.Value)}",
                        $"menu{mem.Name}:{chatId}"));
                }
                else if (mem.Value.Equals("allowed"))
                {
                    mainMenu.Buttons.Add(new InlineButton("✅", $"menu{mem.Name}:{chatId}"));
                }
            }
            var max = Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "max").Result;
            var action = Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "type").Result;
            var warnTitle = new Menu(1);

            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"FloodButton"),
                $"openFloodMenu:{chatId}"));
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "lengthButton"), $"openLengthMenu:{chatId}"));
            var nsfw = Redis.db.HashGetAsync($"chat:{chatId}:nsfwDetection", "autherised").Result;
            if (nsfw.HasValue && nsfw.ToString().Equals("yes"))
            {
                warnTitle.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "nsfwButton"), $"opennsfwmenu:{chatId}"));
            }        
            warnTitle.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "WarnsButton"), "menualert:warns"));
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "mediaMenuHeader"), $"openMediaMenu:{chatId}"));
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "groupLanguage"), $"openLangMenu:{chatId}"));
            var editWarn = new Menu(3);
            editWarn.Buttons.Add(new InlineButton("➖", $"menuDimWarn:{chatId}"));
            editWarn.Buttons.Add(new InlineButton($"📍 {max} 🔨 {action}", $"menuActionWarn:{chatId}"));
            editWarn.Buttons.Add(new InlineButton("➕", $"menuRaiseWarn:{chatId}"));
            var close = new Menu(1);
            close.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "closeButton"), "close"));
            return Key.CreateMarkupFromMenus(mainMenu, warnTitle, editWarn, close);
        }

        public static InlineKeyboardMarkup genFlood(long chatId, XDocument lang)
        {
            var flood = Redis.db.HashGetAsync($"chat:{chatId}:settings", "Flood").Result;
            var oneRow = new Menu(2);
            if (flood.Equals("no"))
            {
                oneRow.Buttons.Add(new InlineButton($"✅ | {Methods.GetLocaleString(lang, "on")}",
                    $"floodstatus:{chatId}"));
            }
            else if (flood.Equals("yes"))
            {
                oneRow.Buttons.Add(new InlineButton($"❌ | {Methods.GetLocaleString(lang, "off")}",
                    $"floodstatus:{chatId}"));
            }
            var settings = Redis.db.HashGetAllAsync($"chat:{chatId}:flood").Result;
            var action = settings.Where(e => e.Name.Equals("ActionFlood")).FirstOrDefault();
            oneRow.Buttons.Add(action.Value.Equals("kick")
                ? new InlineButton($"⚡️ | {Methods.GetLocaleString(lang, "kick")}", $"floodaction:{chatId}")
                : new InlineButton($"⛔ | {Methods.GetLocaleString(lang, "ban")}", $"floodaction:{chatId}"));
            action = settings.Where(e => e.Name.Equals("MaxFlood")).FirstOrDefault();
            var twoRow = new Menu(3);
            twoRow.Buttons.Add(new InlineButton("➖", $"flooddim:{chatId}"));
            twoRow.Buttons.Add(new InlineButton(action.Value, $"floodSettings:{chatId}"));
            twoRow.Buttons.Add(new InlineButton("➕", $"floodraise:{chatId}"));
            var exceptions = Redis.db.HashGetAllAsync($"chat:{chatId}:floodexceptions").Result;
            var exeMenu = new Menu(2);
            var textFound = false;
            foreach (var mem in exceptions)
            {
                exeMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{mem.Name}Button"),
                    $"floodSettings:{mem.Name}"));
                if (mem.Name.Equals("text"))
                    textFound = true;
                if (mem.Value.Equals("yes"))
                {
                    exeMenu.Buttons.Add(new InlineButton("✅", $"flood{mem.Name}:{chatId}"));
                }
                else 
                {
                    exeMenu.Buttons.Add(new InlineButton("❌", $"flood{mem.Name}:{chatId}"));
                }
            }
            if (!textFound)
            {
                exeMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"textButton"),
                   $"floodSettings:text"));
                exeMenu.Buttons.Add(new InlineButton("❌", $"floodtext:{chatId}"));
            }
            var close = new Menu(1);
            close.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "backButton"), $"back:{chatId}"));
            return Key.CreateMarkupFromMenus(oneRow, twoRow, exeMenu, close);
        }

        public static InlineKeyboardMarkup genMediaMenu(long chatId, XDocument lang)
            {
            var mediaList = Redis.db.HashGetAllAsync($"chat:{chatId}:media").Result;
            var menu = new Menu(2);
            foreach (var mem in mediaList)
            {
                menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{mem.Name}Button"),
                    $"mediasettings:{mem.Name}"));
                if (mem.Value.Equals("kick") || mem.Value.Equals("ban"))
                {
                    menu.Buttons.Add(new InlineButton($"🔐 {Methods.GetLocaleString(lang, mem.Value)}",
                        $"media{mem.Name}:{chatId}"));
                }
                else if (mem.Value.Equals("allowed"))
                {
                    menu.Buttons.Add(new InlineButton("✅", $"media{mem.Name}:{chatId}"));
                }
            }
            var max = Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "mediamax").Result;
            var warnTitle = new Menu(1);
            warnTitle.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "WarnsButton"), "menualert:warns"));
            var editWarn = new Menu(3);
            editWarn.Buttons.Add(new InlineButton("➖", $"mediaDimWarn:{chatId}"));
            editWarn.Buttons.Add(new InlineButton($"📍 {max}", $"mediaActionWarn:{chatId}"));
            editWarn.Buttons.Add(new InlineButton("➕", $"mediaRaiseWarn:{chatId}"));
            var close = new Menu(1);
            close.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "backButton"), $"back:{chatId}"));
            return Key.CreateMarkupFromMenus(menu, editWarn, close);
        }

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

        public static InlineKeyboardMarkup genAntiLengthMenu(long chatId, XDocument lang)
        {
            var nameSettings = Redis.db.HashGetAllAsync($"chat:{chatId}:antinamelengthsettings").Result;
            var textSettings = Redis.db.HashGetAllAsync($"chat:{chatId}:antitextlengthsettings").Result;

            var menu = new Menu(2);
            menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "nameSettingsHeader")));
            menu.Buttons.Add(new InlineButton($"{nameSettings.Where(e => e.Name.Equals("maxlength")).FirstOrDefault().Value}"));
            foreach (var mem in nameSettings)
            {
                if (mem.Value.Equals("yes"))
                {
                    menu.Buttons.Add(new InlineButton($"✅ | {Methods.GetLocaleString(lang, "on")}",
                        $"namesettings:{chatId}"));
                }
                else if (mem.Value.Equals("no"))
                {
                    menu.Buttons.Add(new InlineButton($"❌ | {Methods.GetLocaleString(lang, "off")}",
                        $"namesettings:{chatId}"));
                }
                if (mem.Value.Equals("kick") || mem.Value.Equals("ban") || mem.Value.Equals("Warn"))
                {
                    menu.Buttons.Add(new InlineButton($"{Methods.GetLocaleString(lang, mem.Value)}",
                        $"namesettings{mem.Name}:{chatId}"));
                }
            }
            menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "textSettingsHeader")));
            menu.Buttons.Add(new InlineButton($"{textSettings.Where(e => e.Name.Equals("maxlength")).FirstOrDefault().Value} : {textSettings.Where(e => e.Name.Equals("maxlines")).FirstOrDefault().Value}"));
            foreach (var mem in textSettings)
            {
                if (mem.Value.Equals("yes"))
                {
                    menu.Buttons.Add(new InlineButton($"✅ | {Methods.GetLocaleString(lang, "on")}",
                        $"textsettings:{chatId}"));
                }
                else if (mem.Value.Equals("no"))
                {
                    menu.Buttons.Add(new InlineButton($"❌ | {Methods.GetLocaleString(lang, "off")}",
                        $"textsettings:{chatId}"));
                }
                else if (mem.Value.Equals("kick") || mem.Value.Equals("ban") || mem.Value.Equals("Warn"))
                {
                    menu.Buttons.Add(new InlineButton($"{Methods.GetLocaleString(lang, mem.Value)}",
                        $"textsettings{mem.Name}:{chatId}"));
                }
            }
            var close = new Menu(1);
            close.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "backButton"), $"back:{chatId}"));
            return Key.CreateMarkupFromMenus(menu, close);
        }
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "openLengthMenu", GroupAdminOnly = true)]
        public static void openLengthMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId);
            var text = Methods.GetLocaleString(lang.Doc, "lengthMenu", lang.Base);
            var keys = Commands.genAntiLengthMenu(chatId, lang.Doc);
             Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, text, replyMarkup: keys, parseMode: ParseMode.Html);
        }

        [Callback(Trigger = "namesettingsaction")]
        public static void NameSettingsAction(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "action";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:antinamelengthsettings", option).Result;
            if (current.Equals("ban"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:antinamelengthsettings", option, "kick");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:antinamelengthsettings", option, "Warn");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("Warn"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:antinamelengthsettings", option, "kick");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "namesettings")]
        public static void NameSettings(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "enabled";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:antinamelengthsettings", option).Result;
            if (current.Equals("yes"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:antinamelengthsettings", option, "no");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:antinamelengthsettings", option, "yes");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "textsettingsaction")]
        public static void TextSettingsAction(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "action";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:antitextlengthsettings", option).Result;
            if (current.Equals("ban"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:antitextlengthsettings", option, "kick");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:antitextlengthsettings", option, "Warn");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("Warn"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:antitextlengthsettings", option, "kick");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "textsettings")]
        public static void TextSettings(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "enabled";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:antitextlengthsettings", option).Result;
            if (current.Equals("yes"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:antitextlengthsettings", option, "no");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:antitextlengthsettings", option, "yes");
                var keys = Commands.genAntiLengthMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

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
             Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, replyMarkup:keys, text:menuText);
        }

        [Callback(Trigger = "menusettings")]
        public static void MenuChanges(CallbackQuery call, string[] args)
        {
             Bot.Api.AnswerCallbackQueryAsync(call.Id, "Still coming");
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
                var keys = Commands.genMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
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
                var keys = Commands.genMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
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
                var keys = Commands.genMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
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
                var keys = Commands.genMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
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
                var keys = Commands.genMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
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
                var keys = Commands.genMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
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
                var keys = Commands.genMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("no"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:settings", option, "yes");
                var keys = Commands.genMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
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
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:char", option, "allowed");
                var keys = Commands.genMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("allowed"))
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
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:char", option, "allowed");
                var keys = Commands.genMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("allowed"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:char", option, "ban");
                var keys = Commands.genMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(chatId, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "menuDimWarn")]
        public static void menuDimWarn(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var num = Redis.db.HashDecrementAsync($"chat:{chatId}:warnsettings", "max").Result;
            if (num > 0)
            {
                var keys = Commands.genMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else
            {
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "warnToLow"));
                 Redis.db.HashIncrementAsync($"chat:{chatId}:warnsettings", "max");
            }
        }

        [Callback(Trigger = "menuRaiseWarn")]
        public static void menuRaiseWarn(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
             Redis.db.HashIncrementAsync($"chat:{chatId}:warnsettings", "max");
            var keys = Commands.genMenu(chatId, lang);
             Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
             Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
        }

        [Callback(Trigger = "menuActionWarn")]
        public static void menuActionWarn(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "type";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", option).Result;
            if (current.Equals("ban"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:warnsettings", option, "kick");
                var keys = Commands.genMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:warnsettings", option, "ban");
                var keys = Commands.genMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "openFloodMenu", GroupAdminOnly = true)]
        public static void openFloodMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var text = Methods.GetLocaleString(lang, "floodMenu");
            var keys = Commands.genFlood(chatId, lang);
             Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, text, replyMarkup: keys);
        }

        [Callback(Trigger = "openMediaMenu", GroupAdminOnly = true)]
        public static void openMediaMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var text = Methods.GetLocaleString(lang, "mediaMenu");
            var keys = Commands.genMediaMenu(chatId, lang);
             Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, text, replyMarkup: keys, parseMode: ParseMode.Html);
        }

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

        [Callback(Trigger = "floodSettings")]
        public static void floodSettings(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
             Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "doNothing"));
        }

        [Callback(Trigger = "flooddim")]
        public static void flooddim(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var num = Redis.db.HashDecrementAsync($"chat:{chatId}:flood", "MaxFlood").Result;
            if (num > 4)
            {
                var keys = Commands.genFlood(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else
            {
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "warnToLow"));
                 Redis.db.HashIncrementAsync($"chat:{chatId}:flood", "MaxFlood");
            }
        }

        [Callback(Trigger = "floodraise")]
        public static void floodraise(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
             Redis.db.HashIncrementAsync($"chat:{chatId}:flood", "MaxFlood");
            var keys = Commands.genFlood(chatId, lang);
             Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
             Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
        }

        [Callback(Trigger = "floodaction")]
        public static void floodaction(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "ActionFlood";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:flood", option).Result;
            if (current.Equals("ban"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:flood", option, "kick");
                var keys = Commands.genFlood(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:flood", option, "ban");
                var keys = Commands.genFlood(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "floodtext")]
        public static void floodtext(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "text";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:floodexceptions", option).Result;
            if (current.Equals("yes"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:floodexceptions", option, "no");
                var keys = Commands.genFlood(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else 
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:floodexceptions", option, "yes");
                var keys = Commands.genFlood(call.From.Id, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }
        [Callback(Trigger = "floodsticker")]
        public static void floodsticker(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "sticker";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:floodexceptions", option).Result;
            if (current.Equals("yes"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:floodexceptions", option, "no");
                var keys = Commands.genFlood(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:floodexceptions", option, "yes");
                var keys = Commands.genFlood(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "floodimage")]
        public static void floodimage(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "image";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:floodexceptions", option).Result;
            if (current.Equals("yes"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:floodexceptions", option, "no");
                var keys = Commands.genFlood(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:floodexceptions", option, "yes");
                var keys = Commands.genFlood(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "floodvideo")]
        public static void floodvideo(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "video";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:floodexceptions", option).Result;
            if (current.Equals("yes"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:floodexceptions", option, "no");
                var keys = Commands.genFlood(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:floodexceptions", option, "yes");
                var keys = Commands.genFlood(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "floodgif")]
        public static void floodgif(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "gif";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:floodexceptions", option).Result;
            if (current.Equals("yes"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:floodexceptions", option, "no");
                var keys = Commands.genFlood(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:floodexceptions", option, "yes");
                var keys = Commands.genFlood(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "mediaDimWarn")]
        public static void mediaDimWarn(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var num = Redis.db.HashDecrementAsync($"chat:{chatId}:warnsettings", "mediamax").Result;
            if (num > 0)
            {
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else
            {
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "warnToLow"));
                 Redis.db.HashIncrementAsync($"chat:{chatId}:warnsettings", "mediamax");
            }
        }

        [Callback(Trigger = "mediaRaiseWarn")]
        public static void mediaRaiseWarn(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
             Redis.db.HashIncrementAsync($"chat:{chatId}:warnsettings", "mediamax");
            var keys = Commands.genMediaMenu(chatId, lang);
             Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
             Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
        }

        [Callback(Trigger = "mediatext")]
        public static void mediatext(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "text";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:media", option).Result;
            if (current.Equals("ban"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "kick");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "allowed");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("allowed"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "ban");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }
        [Callback(Trigger = "mediasticker")]
        public static void mediasticker(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "sticker";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:media", option).Result;
            if (current.Equals("ban"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "kick");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "allowed");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("allowed"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "ban");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "mediaimage")]
        public static void mediaimage(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "image";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:media", option).Result;
            if (current.Equals("ban"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "kick");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "allowed");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("allowed"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "ban");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "mediavideo")]
        public static void mediavideo(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "video";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:media", option).Result;
            if (current.Equals("ban"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "kick");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "allowed");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("allowed"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "ban");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "mediagif")]
        public static void mediagif(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "gif";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:media", option).Result;
            if (current.Equals("ban"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "kick");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "allowed");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("allowed"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "ban");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "mediacontact")]
        public static void mediacontact(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "contact";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:media", option).Result;
            if (current.Equals("ban"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "kick");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "allowed");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("allowed"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "ban");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "mediafile")]
        public static void mediafile(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "file";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:media", option).Result;
            if (current.Equals("ban"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "kick");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "allowed");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("allowed"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "ban");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "medialink")]
        public static void medialink(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "link";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:media", option).Result;
            if (current.Equals("ban"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "kick");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "allowed");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("allowed"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "ban");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "mediavoice")]
        public static void mediavoice(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "voice";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:media", option).Result;
            if (current.Equals("ban"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "kick");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "allowed");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("allowed"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "ban");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "mediaaudio")]
        public static void mediaaudio(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "audio";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:media", option).Result;
            if (current.Equals("ban"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "kick");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "allowed");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("allowed"))
            {
                 Redis.db.HashSetAsync($"chat:{chatId}:media", option, "ban");
                var keys = Commands.genMediaMenu(chatId, lang);
                 Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                 Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
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
        [Callback(Trigger = "nsfwaction")]
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

        [Callback(Trigger = "nsfwsettings")]
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
