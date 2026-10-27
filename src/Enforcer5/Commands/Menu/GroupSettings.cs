using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Data;
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
            var policy = Detection.DetectionSettings.For(chatId);
            var mainMenu = new Menu();
            mainMenu.Columns = 2;
            mainMenu.Buttons = new List<InlineButton>();
            // Flood and Mute On Join are in the detection settings (PostgreSQL) since Detection step 2.
            void OnOff(string name, bool on)
            {
                mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{name}Button"), $"menusettings:{name}"));
                mainMenu.Buttons.Add(new InlineButton(on ? "✅" : "🚫", $"menu{name}:{chatId}"));
            }
            OnOff("Flood", policy.Flood.Enabled);
            foreach (var mem in settings)
            {
                if (mem.Name.Equals("Report") | mem.Name.Equals("Welcome") | mem.Name.Equals("DeleteLastWelcome"))
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
            OnOff("MuteOnJoin", policy.MuteOnJoin);
            foreach (var (name, action) in new[] { ("Arab", policy.Scripts.Arabic), ("Rtl", policy.Scripts.Rtl) })
            {
                mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{name}Button"),
                    $"menusettings:{name}"));
                switch (action)
                {
                    case "kick":
                        mainMenu.Buttons.Add(new InlineButton($"⚡️ | {Methods.GetLocaleString(lang, "kick")}", $"menu{name}:{chatId}"));
                        break;
                    case "ban":
                        mainMenu.Buttons.Add(new InlineButton($"⛔ | {Methods.GetLocaleString(lang, "ban")}", $"menu{name}:{chatId}"));
                        break;
                    case "tempban":
                        mainMenu.Buttons.Add(new InlineButton($"⏳ | {Methods.GetLocaleString(lang, "tempban")}", $"menu{name}:{chatId}"));
                        break;
                    default:
                        mainMenu.Buttons.Add(new InlineButton("✅", $"menu{name}:{chatId}"));
                        break;
                }
            }
            var channelPostsBlocked = policy.ChannelPosts.Enabled;
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "ChannelPostsButton"),
                "menusettings:ChannelPosts"));
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, channelPostsBlocked ? "on" : "off"),
                $"menuChannelPosts:{chatId}"));
            var warnReasonMode = Repositories.WarnReasons.GetModeAsync(chatId).GetAwaiter().GetResult();
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "WarnReasonsButton"),
                "menusettings:WarnReasons"));
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"warnReasonMode{warnReasonMode}"),
                $"menuWarnReasons:{chatId}"));
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
        public static void MenuFlood(CallbackQuery call, string[] args) =>
            ChangeSettings(call, args, p => p with { Flood = p.Flood with { Enabled = !p.Flood.Enabled } });

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
        public static void MenuMuteOnJoin(CallbackQuery call, string[] args) =>
            ChangeSettings(call, args, p => p with { MuteOnJoin = !p.MuteOnJoin });

        // The script presets cycle ban -> kick -> allowed -> tempban -> ban, as they always did.
        private static readonly string[] ScriptActions = { "kick", null, "tempban", "ban" };

        [Callback(Trigger = "menuRtl", GroupAdminOnly = true)]
        public static void MenuRtl(CallbackQuery call, string[] args) =>
            ChangeSettings(call, args, p => p with { Scripts = p.Scripts with { Rtl = Next(p.Scripts.Rtl, ScriptActions) } });

        [Callback(Trigger = "menuArab", GroupAdminOnly = true)]
        public static void MenuArab(CallbackQuery call, string[] args) =>
            ChangeSettings(call, args, p => p with { Scripts = p.Scripts with { Arabic = Next(p.Scripts.Arabic, ScriptActions) } });

        [Callback(Trigger = "menuChannelPosts", GroupAdminOnly = true)]
        public static void MenuChannelPosts(CallbackQuery call, string[] args) =>
            ChangeSettings(call, args, p => p with { ChannelPosts = p.ChannelPosts with { Enabled = !p.ChannelPosts.Enabled } });

        /// <summary>Changes the group's detection settings and redraws the settings menu.</summary>
        private static void ChangeSettings(CallbackQuery call, string[] args, Func<Detection.DetectionPolicy, Detection.DetectionPolicy> change)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            if (Detection.DetectionSettings.TryUpdate(chatId, change) == null)
            {
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "failed"));
                return;
            }
            RedrawSettingsMenu(call, chatId, lang);
        }

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
                // The menu message may be too old to edit (400) - fall back to sending a fresh one.
                if (Bot.AsApiError(e)?.ErrorCode != 400)
                    ErrorReporter.Report(e, $"Redrawing the settings menu of {chatId}");
                Bot.Send(call.Message.Text, call.From.Id, customMenu: keys);
            }
        }
    }
}
