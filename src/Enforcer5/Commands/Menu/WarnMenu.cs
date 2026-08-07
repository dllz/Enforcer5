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
using Enforcer5.Storage;

namespace Enforcer5
{
    public static partial class Commands
    {
        public static InlineKeyboardMarkup genWarnMenu(long chatId, XDocument lang)
        {
            var settings = GroupSettingsStore.GetAllAsync(chatId, GroupSettingGroups.Settings).Result;
            var mainMenu = new Models.Menu();
            mainMenu.Columns = 2;
            mainMenu.Buttons = new List<InlineButton>();

            var max = GroupSettingsStore.GetAsync(chatId, GroupSettingGroups.Warn, "max").Result;
            var action = GroupSettingsStore.GetAsync(chatId, GroupSettingGroups.Warn, "type").Result;
            var editWarn = new Menu(3);
                     
            editWarn.Buttons.Add(new InlineButton("➖", $"menuDimWarn:{chatId}"));
            editWarn.Buttons.Add(new InlineButton($"📍 {max} 🔨 {action}", $"menuActionWarn:{chatId}"));
            editWarn.Buttons.Add(new InlineButton("➕", $"menuRaiseWarn:{chatId}"));
            var close = new Models.Menu(1);
            close.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "backButton"), $"back:{chatId}"));
            return Key.CreateMarkupFromMenus(editWarn, close);
        }
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "openWarnMenu")]
        public static void openWarnMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var text = Methods.GetLocaleString(lang, "warnMenu");
            var keys = Commands.genWarnMenu(chatId, lang);
            Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, text, replyMarkup: keys);
        }

        [Callback(Trigger = "menuDimWarn", GroupAdminOnly = true)]
        public static void menuDimWarn(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var num = GroupSettingsStore.DecrementAsync(chatId, GroupSettingGroups.Warn, "max").GetAwaiter().GetResult();
            if (num > 0)
            {
                var keys = Commands.genWarnMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else
            {
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "warnToLow"));
                GroupSettingsStore.IncrementAsync(chatId, GroupSettingGroups.Warn, "max").GetAwaiter().GetResult();
            }
        }

        [Callback(Trigger = "menuRaiseWarn", GroupAdminOnly = true)]
        public static void menuRaiseWarn(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            GroupSettingsStore.IncrementAsync(chatId, GroupSettingGroups.Warn, "max").GetAwaiter().GetResult();
            var keys = Commands.genWarnMenu(chatId, lang);
            Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
            Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
        }

        [Callback(Trigger = "menuActionWarn", GroupAdminOnly = true)]
        public static void menuActionWarn(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "type";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = GroupSettingsStore.GetAsync(chatId, GroupSettingGroups.Warn, option).Result;
            if (current.Equals("ban"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Warn, option, "kick").GetAwaiter().GetResult();
                var keys = Commands.genWarnMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Warn, option, "ban").GetAwaiter().GetResult();
                var keys = Commands.genWarnMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text,
                    replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }
    }
}
