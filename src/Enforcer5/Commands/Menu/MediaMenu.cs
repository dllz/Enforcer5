using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot;
using Enforcer5.Storage;

namespace Enforcer5
{
    public static partial class Commands
    {
        public static InlineKeyboardMarkup genMediaMenu(long chatId, XDocument lang)
        {
            var mediaList = GroupSettingsStore.GetAllAsync(chatId, GroupSettingGroups.Media).Result;
            var menu = new Menu(2);
            
            foreach (var mem in mediaList)
            {
                
                if (!mem.Name.Equals("action"))
                {
                    menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{mem.Name}Button"),
                        $"mediasettings:{mem.Name}"));
                    switch (mem.Value.ToString())
                    {
                        case "allowed":
                            menu.Buttons.Add(new InlineButton("✅", $"media{mem.Name}:{chatId}"));
                            break;
                        case "blocked":
                            menu.Buttons.Add(new InlineButton("❌", $"media{mem.Name}:{chatId}"));
                            break;
                    }
                }
            }
            var max = GroupSettingsStore.GetAsync(chatId, GroupSettingGroups.Warn, "mediamax").Result;
            var warnTitle = new Menu(1);
            var action = GroupSettingsStore.GetAsync(chatId, GroupSettingGroups.Media, "action").Result;

            switch (action.ToString())
            {
                case "kick":
                    warnTitle.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "WarnsButton", $"⚡️ | {Methods.GetLocaleString(lang, "kick")}"), $"mediaaction:{chatId}"));
                    break;
                case "ban":
                    warnTitle.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "WarnsButton", $"⛔ | {Methods.GetLocaleString(lang, "ban")}"), $"mediaaction:{chatId}"));
                    break;
                case "tempban":
                    warnTitle.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "WarnsButton", $"⏳ | {Methods.GetLocaleString(lang, "tempban")}"), $"mediaaction:{chatId}"));
                    break;

            }            
            var editWarn = new Menu(3);
            editWarn.Buttons.Add(new InlineButton("➖", $"mediaDimWarn:{chatId}"));
            editWarn.Buttons.Add(new InlineButton($"📍 {max}", $"mediaActionWarn:{chatId}"));
            editWarn.Buttons.Add(new InlineButton("➕", $"mediaRaiseWarn:{chatId}"));
            var close = new Menu(1);
            close.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "backButton"), $"back:{chatId}"));
            return Key.CreateMarkupFromMenus(menu, warnTitle, editWarn, close);
        }      
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "openMediaMenu")]
        public static void openMediaMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var text = Methods.GetLocaleString(lang, "mediaMenu");
            var keys = Commands.genMediaMenu(chatId, lang);
            Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, text, replyMarkup: keys, parseMode: ParseMode.Html);
        }

        [Callback(Trigger = "mediaaction", GroupAdminOnly = true)]
        public static void mediaAction(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "action";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = GroupSettingsStore.GetAsync(chatId, GroupSettingGroups.Media, option).Result;
            if (current.Equals("ban"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "kick").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("kick"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "tempban").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("tempban"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "ban").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "mediaDimWarn", GroupAdminOnly = true)]
        public static void mediaDimWarn(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var num = GroupSettingsStore.DecrementAsync(chatId, GroupSettingGroups.Warn, "mediamax").GetAwaiter().GetResult();
            if (num > 0)
            {
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else
            {
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "warnToLow"));
                GroupSettingsStore.IncrementAsync(chatId, GroupSettingGroups.Warn, "mediamax").GetAwaiter().GetResult();
            }
        }

        [Callback(Trigger = "mediaRaiseWarn", GroupAdminOnly = true)]
        public static void mediaRaiseWarn(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            GroupSettingsStore.IncrementAsync(chatId, GroupSettingGroups.Warn, "mediamax").GetAwaiter().GetResult();
            var keys = Commands.genMediaMenu(chatId, lang);
            Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
            Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
        }

        [Callback(Trigger = "mediatext", GroupAdminOnly = true)]
        public static void mediatext(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "text";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = GroupSettingsStore.GetAsync(chatId, GroupSettingGroups.Media, option).Result;
            if (current.Equals("allowed"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "blocked").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("blocked"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "allowed").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }
        [Callback(Trigger = "mediasticker", GroupAdminOnly = true)]
        public static void mediasticker(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "sticker";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = GroupSettingsStore.GetAsync(chatId, GroupSettingGroups.Media, option).Result;
            if (current.Equals("allowed"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "blocked").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("blocked"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "allowed").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "mediaimage", GroupAdminOnly = true)]
        public static void mediaimage(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "image";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = GroupSettingsStore.GetAsync(chatId, GroupSettingGroups.Media, option).Result;
            if (current.Equals("allowed"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "blocked").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("blocked"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "allowed").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "mediavideo", GroupAdminOnly = true)]
        public static void mediavideo(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "video";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = GroupSettingsStore.GetAsync(chatId, GroupSettingGroups.Media, option).Result;
            if (current.Equals("allowed"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "blocked").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("blocked"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "allowed").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "mediagif", GroupAdminOnly = true)]
        public static void mediagif(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "gif";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = GroupSettingsStore.GetAsync(chatId, GroupSettingGroups.Media, option).Result;
            if (current.Equals("allowed"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "blocked").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("blocked"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "allowed").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "mediacontact", GroupAdminOnly = true)]
        public static void mediacontact(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "contact";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = GroupSettingsStore.GetAsync(chatId, GroupSettingGroups.Media, option).Result;
            if (current.Equals("allowed"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "blocked").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("blocked"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "allowed").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "mediafile", GroupAdminOnly = true)]
        public static void mediafile(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "file";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = GroupSettingsStore.GetAsync(chatId, GroupSettingGroups.Media, option).Result;
            if (current.Equals("allowed"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "blocked").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("blocked"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "allowed").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "medialink", GroupAdminOnly = true)]
        public static void medialink(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "link";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = GroupSettingsStore.GetAsync(chatId, GroupSettingGroups.Media, option).Result;
            if (current.Equals("allowed"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "blocked").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("blocked"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "allowed").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "mediavoice", GroupAdminOnly = true)]
        public static void mediavoice(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "voice";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = GroupSettingsStore.GetAsync(chatId, GroupSettingGroups.Media, option).Result;
            if (current.Equals("allowed"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "blocked").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("blocked"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "allowed").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }

        [Callback(Trigger = "mediaaudio", GroupAdminOnly = true)]
        public static void mediaaudio(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "audio";
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var current = GroupSettingsStore.GetAsync(chatId, GroupSettingGroups.Media, option).Result;
            if (current.Equals("allowed"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "blocked").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("blocked"))
            {
                GroupSettingsStore.SetAsync(chatId, GroupSettingGroups.Media, option, "allowed").GetAwaiter().GetResult();
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }
    }
}
