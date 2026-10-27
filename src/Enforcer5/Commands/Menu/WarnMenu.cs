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
        public static InlineKeyboardMarkup genWarnMenu(long chatId, XDocument lang)
        {
            var settings = Redis.db.HashGetAllAsync($"chat:{chatId}:settings").Result;
            var mainMenu = new Models.Menu();
            mainMenu.Columns = 2;
            mainMenu.Buttons = new List<InlineButton>();

            var warns = Detection.DetectionSettings.For(chatId).Warns;
            var max = warns.MaxWarns;
            var action = warns.Action;
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
            if (Detection.DetectionSettings.For(chatId).Warns.MaxWarns <= 1)
            {
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "warnToLow"));
                return;
            }
            ChangeDetection(call, chatId, lang,
                p => p with { Warns = p.Warns with { MaxWarns = Math.Max(1, p.Warns.MaxWarns - 1) } }, Commands.genWarnMenu);
        }

        [Callback(Trigger = "menuRaiseWarn", GroupAdminOnly = true)]
        public static void menuRaiseWarn(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            ChangeDetection(call, chatId, lang,
                p => p with { Warns = p.Warns with { MaxWarns = Math.Min(short.MaxValue, p.Warns.MaxWarns + 1) } }, Commands.genWarnMenu);
        }

        [Callback(Trigger = "menuActionWarn", GroupAdminOnly = true)]
        public static void menuActionWarn(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            ChangeDetection(call, chatId, lang,
                p => p with { Warns = p.Warns with { Action = Next(p.Warns.Action, Detection.WarnSettings.Kick, Detection.WarnSettings.Ban) } },
                Commands.genWarnMenu);
        }
    }
}
