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
            switch (action.Value.ToString())
            {
                case "kick":
                    oneRow.Buttons.Add(new InlineButton($"⚡️ | {Methods.GetLocaleString(lang, "kick")}", $"floodaction:{chatId}"));
                    break;
                case "ban":
                    oneRow.Buttons.Add(new InlineButton($"⛔ | {Methods.GetLocaleString(lang, "ban")}", $"floodaction:{chatId}"));
                    break;
                case "warn":
                    oneRow.Buttons.Add(new InlineButton($"⚠️ | {Methods.GetLocaleString(lang, "Warn")}", $"floodaction:{chatId}"));
                    break;
                case "tempban":
                    oneRow.Buttons.Add(new InlineButton($"⏳ | {Methods.GetLocaleString(lang, "tempban")}", $"floodaction:{chatId}"));
                    break;

            }
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
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "openFloodMenu", GroupAdminOnly = true)]
        public static void openFloodMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var text = Methods.GetLocaleString(lang, "floodMenu");
            var keys = Commands.genFlood(chatId, lang);
            Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, text, replyMarkup: keys);
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
                Redis.db.HashSetAsync($"chat:{chatId}:flood", option, "warn");
                var keys = Commands.genFlood(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            } else if (current.Equals("warn"))
            {
                Redis.db.HashSetAsync($"chat:{chatId}:flood", option, "tempban");
                var keys = Commands.genFlood(chatId, lang);
                Bot.Api.EditMessageTextAsync(call.From.Id, call.Message.MessageId, call.Message.Text, replyMarkup: keys);
                Bot.Api.AnswerCallbackQueryAsync(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            }else if (current.Equals("tempban"))
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
    }
}
