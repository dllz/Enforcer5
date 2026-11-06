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
        /// <summary>The settings that answer in private (👤) or in the group (👥), by their menu name.</summary>
        internal static readonly (string Name, ConfigFlag Flag)[] PrivateFlags =
        {
            ("Rules", ConfigFlag.RulesPrivate), ("About", ConfigFlag.AboutPrivate), ("Modlist", ConfigFlag.ModlistPrivate),
            ("Extra", ConfigFlag.ExtraPrivate), ("Help", ConfigFlag.HelpPrivate)
        };

        public static InlineKeyboardMarkup genGroupSettingsMenu(long chatId, XDocument lang)
        {
            var policy = Detection.DetectionSettings.For(chatId);
            var mainMenu = new Menu();
            mainMenu.Columns = 2;
            mainMenu.Buttons = new List<InlineButton>();
            // Flood and Mute On Join are in the detection settings (PostgreSQL) since Detection step 2.
            void OnOff(string name, bool on)
            {
                mainMenu.Buttons.Add(MenuInfo.Label(Methods.GetLocaleString(lang, $"{name}Button"), chatId, name));
                mainMenu.Buttons.Add(new InlineButton(on ? "✅" : "🚫", $"menu{name}:{chatId}") { Style = on ? Telegram.Bot.Types.ReplyMarkups.KeyboardButtonStyle.Success : null });
            }
            OnOff("Flood", policy.Flood.Enabled);
            // The group's config (rollout step 15): every setting, always; the old hash showed only the fields it had.
            var config = GroupConfigs.For(chatId);
            OnOff("Report", config.Flag(ConfigFlag.Reports));
            OnOff("Welcome", config.Flag(ConfigFlag.Welcome));
            OnOff("DeleteLastWelcome", config.Flag(ConfigFlag.DeleteLastWelcome));
            foreach (var (name, flag) in PrivateFlags)
            {
                mainMenu.Buttons.Add(MenuInfo.Label(Methods.GetLocaleString(lang, $"{name}Button"), chatId, "sendIn", name));
                mainMenu.Buttons.Add(new InlineButton(config.Flag(flag) ? "👤" : "👥", $"menu{name}:{chatId}"));
            }
            OnOff("MuteOnJoin", policy.MuteOnJoin);
            var channelPostsBlocked = policy.ChannelPosts.Enabled;
            mainMenu.Buttons.Add(MenuInfo.Label(Methods.GetLocaleString(lang, "ChannelPostsButton"), chatId, "ChannelPosts"));
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, channelPostsBlocked ? "on" : "off"),
                $"menuChannelPosts:{chatId}"));
            var warnReasonMode = Repositories.WarnReasons.GetModeAsync(chatId).GetAwaiter().GetResult();
            mainMenu.Buttons.Add(MenuInfo.Label(Methods.GetLocaleString(lang, "WarnReasonsButton"), chatId, "WarnReasons"));
            mainMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"warnReasonMode{warnReasonMode}"),
                $"menuWarnReasons:{chatId}"));
            // A setting's name explains it (MenuInfo); its value changes it.
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
            Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, text, replyMarkup: keys).Forget();
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
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "failed")).Forget();
                return;
            }
            RedrawSettingsMenu(call, chatId, lang);
        }

        /// <summary>Flips one of the group's on/off settings (its config) and redraws the settings menu.</summary>
        private static void ToggleSetting(CallbackQuery call, string[] args, string option)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var flag = option switch
            {
                "Report" => ConfigFlag.Reports,
                "Welcome" => ConfigFlag.Welcome,
                "DeleteLastWelcome" => ConfigFlag.DeleteLastWelcome,
                _ => Commands.PrivateFlags.Single(p => p.Name == option).Flag
            };
            var on = GroupConfigs.For(chatId).Flag(flag);
            if (!GroupConfigs.TryChange(chatId, ConfigChange.SetFlag(flag, !on)))
            {
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "failed")).Forget();
                return;
            }
            RedrawSettingsMenu(call, chatId, lang);
        }

        private static void RedrawSettingsMenu(CallbackQuery call, long chatId, XDocument lang)
        {
            var keys = Commands.genGroupSettingsMenu(chatId, lang);
            try
            {
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys).Wait();
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged")).Forget();
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
