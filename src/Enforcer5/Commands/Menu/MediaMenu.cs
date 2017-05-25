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

namespace Enforcer5
{
    public static partial class Commands
    {
        public static InlineKeyboardMarkup genMediaMenu(long chatId, XDocument lang)
        {
            var mediaList = Redis.db.HashGetAllAsync($"chat:{chatId}:media").Result;
            var menu = new Menu(2);
            
            foreach (var mem in mediaList)
            {
                menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{mem.Name}Button"),
                    $"mediasettings:{mem.Name}"));
                if (!mem.Name.Equals("action"))
                {
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
            var max = Redis.db.HashGetAsync($"chat:{chatId}:warnsettings", "mediamax").Result;
            var warnTitle = new Menu(1);
            var action = Redis.db.HashGetAsync($"chat:{chatId}:media", "action").Result;

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
            return Key.CreateMarkupFromMenus(menu, editWarn, close);
        }      
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "openMediaMenu", GroupAdminOnly = true)]
        public static void openMediaMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var text = Methods.GetLocaleString(lang, "mediaMenu");
            var keys = Commands.genMediaMenu(chatId, lang);
            Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, text, replyMarkup: keys, parseMode: ParseMode.Html);
        }

        [Callback(Trigger = "mediaaction")]
        public static void mediaAction(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var option = "action";
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
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "tempban");
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("tempban"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "ban");
                var keys = Commands.genMediaMenu(chatId, lang);
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
            if (current.Equals("allowed"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "blocked");
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("blocked"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "allowed");
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
            if (current.Equals("allowed"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "blocked");
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("blocked"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "allowed");
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
            if (current.Equals("allowed"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "blocked");
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("blocked"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "allowed");
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
            if (current.Equals("allowed"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "blocked");
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("blocked"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "allowed");
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
            if (current.Equals("allowed"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "blocked");
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("blocked"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "allowed");
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
            if (current.Equals("allowed"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "blocked");
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("blocked"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "allowed");
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
            if (current.Equals("allowed"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "blocked");
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("blocked"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "allowed");
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
            if (current.Equals("allowed"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "blocked");
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("blocked"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "allowed");
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
            if (current.Equals("allowed"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "blocked");
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("blocked"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "allowed");
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
            if (current.Equals("allowed"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "blocked");
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
            else if (current.Equals("blocked"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:media", option, "allowed");
                var keys = Commands.genMediaMenu(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }
        }
    }
}
