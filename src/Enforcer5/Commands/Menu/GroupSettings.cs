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
using Telegram.Bot;

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
                if (mem.Name.Equals("Flood") | mem.Name.Equals("Report") | mem.Name.Equals("Welcome") | mem.Name.Equals("DeleteLastWelcome") | mem.Name.Equals("MuteOnJoin"))
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
                else if (mem.Name.Equals("Modlist") | mem.Name.Equals("About") | mem.Name.Equals("Rules") |
                         mem.Name.Equals("Extra") | mem.Name.Equals("Help"))
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
            var close = new Menu(1);
            close.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "backButton"), $"back:{chatId}"));
            return Key.CreateMarkupFromMenus(mainMenu, close);
        }
    }

    public static partial class CallBacks
    {

        [Callback(Trigger = "openGroupMenu")]
        public static void openGroupMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var text = Methods.GetLocaleString(lang, "groupMenu");
            var keys = Commands.genGroupSettingsMenu(chatId, lang);
            Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, text, replyMarkup: keys);
        }
        [Callback(Trigger = "menuFlood", GroupAdminOnly = true)]
        public static void MenuFlood(CallbackQuery call, string[] args) => ToggleSetting(call, args, "Flood");

        [Callback(Trigger = "menuReport", GroupAdminOnly = true)]
        public static void MenuReport(CallbackQuery call, string[] args) => ToggleSetting(call, args, "Report");

        [Callback(Trigger = "menuWelcome", GroupAdminOnly = true)]
        public static void MenuWelcome(CallbackQuery call, string[] args) => ToggleSetting(call, args, "Welcome");

        [Callback(Trigger = "menuDeleteLastWelcome", GroupAdminOnly = true)]
        public static void MenuDeleteLastWelcome(CallbackQuery call, string[] args) => ToggleSetting(call, args, "DeleteLastWelcome");

        [Callback(Trigger = "menuModlist", GroupAdminOnly = true)]
        public static void MenuModlist(CallbackQuery call, string[] args) => ToggleSetting(call, args, "Modlist");

        [Callback(Trigger = "menuRules", GroupAdminOnly = true)]
        public static void MenuRules(CallbackQuery call, string[] args) => ToggleSetting(call, args, "Rules");

        [Callback(Trigger = "menuHelp", GroupAdminOnly = true)]
        public static void MenuHelp(CallbackQuery call, string[] args) => ToggleSetting(call, args, "Help");

        [Callback(Trigger = "menuExtra", GroupAdminOnly = true)]
        public static void MenuExtra(CallbackQuery call, string[] args) => ToggleSetting(call, args, "Extra");

        [Callback(Trigger = "menuAbout", GroupAdminOnly = true)]
        public static void MenuAbout(CallbackQuery call, string[] args) => ToggleSetting(call, args, "About");

        [Callback(Trigger = "menuMuteOnJoin", GroupAdminOnly = true)]
        public static void MenuMuteOnJoin(CallbackQuery call, string[] args) => ToggleSetting(call, args, "MuteOnJoin");

        [Callback(Trigger = "menuRtl", GroupAdminOnly = true)]
        public static void MenuRtl(CallbackQuery call, string[] args) => CycleCharSetting(call, args, "Rtl");

        [Callback(Trigger = "menuArab", GroupAdminOnly = true)]
        public static void MenuArab(CallbackQuery call, string[] args) => CycleCharSetting(call, args, "Arab");

        /// <summary>
        /// Flips a yes/no setting in chat:{id}:settings and redraws the settings menu.
        /// </summary>
        private static void ToggleSetting(CallbackQuery call, string[] args, string option)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:settings", option).Result;

            string next;
            if (current.Equals("yes")) next = "no";
            else if (current.Equals("no")) next = "yes";
            else return;

            Redis.db.HashSetAsync($"chat:{chatId}:settings", option, next);
            RedrawSettingsMenu(call, chatId, lang);
        }

        /// <summary>
        /// Advances a character-filter setting in chat:{id}:char through its
        /// ban -> kick -> allowed -> tempban -> ban cycle and redraws the menu.
        /// </summary>
        private static void CycleCharSetting(CallbackQuery call, string[] args, string option)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = Redis.db.HashGetAsync($"chat:{chatId}:char", option).Result;

            string next;
            if (current.Equals("ban")) next = "kick";
            else if (current.Equals("kick")) next = "allowed";
            else if (current.Equals("allowed")) next = "tempban";
            else if (current.Equals("tempban")) next = "ban";
            else return;

            Redis.db.HashSetAsync($"chat:{chatId}:char", option, next);
            RedrawSettingsMenu(call, chatId, lang);
        }

        private static void RedrawSettingsMenu(CallbackQuery call, long chatId, XDocument lang)
        {
            var keys = Commands.genGroupSettingsMenu(chatId, lang);
            try
            {
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys).Wait();
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            catch (Exception e)
            {
                // The menu message may be too old to edit - fall back to sending a fresh one.
                Console.WriteLine(e);
                Bot.Send(call.Message.Text, call.From.Id, customMenu: keys);
            }
        }
    }
}
