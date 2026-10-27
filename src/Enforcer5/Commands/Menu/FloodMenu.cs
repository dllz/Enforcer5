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
        public static InlineKeyboardMarkup genFlood(long chatId, XDocument lang)
        {
            var flood = Detection.DetectionSettings.For(chatId).Flood;
            var oneRow = new Menu(2);
            oneRow.Buttons.Add(flood.Enabled
                ? new InlineButton($"✅ | {Methods.GetLocaleString(lang, "on")}", $"floodstatus:{chatId}")
                : new InlineButton($"❌ | {Methods.GetLocaleString(lang, "off")}", $"floodstatus:{chatId}"));
            switch (flood.Action)
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
            var twoRow = new Menu(3);
            twoRow.Buttons.Add(new InlineButton("➖", $"flooddim:{chatId}"));
            twoRow.Buttons.Add(new InlineButton(flood.MaxMessages?.ToString() ?? "-", $"floodSettings:{chatId}"));
            twoRow.Buttons.Add(new InlineButton("➕", $"floodraise:{chatId}"));
            // The default exceptions, any other type the group set, and text.
            var exeMenu = new Menu(2);
            var types = Detection.FloodSettings.MenuTypes
                .Concat(flood.Counted.Keys.Where(t => t != "text" && !Detection.FloodSettings.MenuTypes.Contains(t)))
                .Append("text");
            foreach (var type in types)
            {
                exeMenu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{type}Button"),
                    $"floodSettings:{type}"));
                exeMenu.Buttons.Add(new InlineButton(flood.IsExempt(type) || type == "text" && !flood.Counted.ContainsKey(type) ? "❌" : "✅",
                    $"flood{type}:{chatId}"));
            }
            var close = new Menu(1);
            close.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "backButton"), $"back:{chatId}"));
            return Key.CreateMarkupFromMenus(oneRow, twoRow, exeMenu, close);
        }
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "openFloodMenu")]
        public static void openFloodMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var text = Methods.GetLocaleString(lang, "floodMenu");
            var keys = Commands.genFlood(chatId, lang);
            Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, text, replyMarkup: keys);
        }

        [Callback(Trigger = "floodstatus", GroupAdminOnly = true)]
        public static void floodStatus(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            ChangeDetection(call, chatId, lang, p => p with { Flood = p.Flood with { Enabled = !p.Flood.Enabled } }, Commands.genFlood);
        }

        [Callback(Trigger = "floodSettings", GroupAdminOnly = true)]
        public static void floodSettings(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "doNothing"));
        }

        /// <summary>The lowest flood limit the menu goes down to.</summary>
        private const int MinimumFlood = 5;

        [Callback(Trigger = "flooddim", GroupAdminOnly = true)]
        public static void flooddim(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var max = Detection.DetectionSettings.For(chatId).Flood.MaxMessages ?? Detection.FloodSettings.DefaultMaxMessages;
            if (max - 1 < MinimumFlood)
            {
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "warnToLow"));
                return;
            }
            ChangeDetection(call, chatId, lang, p => p with
            {
                Flood = p.Flood with { MaxMessages = Math.Max(MinimumFlood, (p.Flood.MaxMessages ?? Detection.FloodSettings.DefaultMaxMessages) - 1) }
            }, Commands.genFlood);
        }

        [Callback(Trigger = "floodraise", GroupAdminOnly = true)]
        public static void floodraise(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            ChangeDetection(call, chatId, lang, p => p with
            {
                Flood = p.Flood with { MaxMessages = (p.Flood.MaxMessages ?? Detection.FloodSettings.DefaultMaxMessages - 1) + 1 }
            }, Commands.genFlood);
        }

        [Callback(Trigger = "floodaction", GroupAdminOnly = true)]
        public static void floodaction(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            ChangeDetection(call, chatId, lang,
                p => p with { Flood = p.Flood with { Action = Next(p.Flood.Action, "kick", "warn", "tempban", "ban") } },
                Commands.genFlood);
        }

        [Callback(Trigger = "floodtext", GroupAdminOnly = true)]
        public static void floodtext(CallbackQuery call, string[] args) => ToggleFloodException(call, args, "text");

        [Callback(Trigger = "floodsticker", GroupAdminOnly = true)]
        public static void floodsticker(CallbackQuery call, string[] args) => ToggleFloodException(call, args, "sticker");

        [Callback(Trigger = "floodimage", GroupAdminOnly = true)]
        public static void floodimage(CallbackQuery call, string[] args) => ToggleFloodException(call, args, "image");

        [Callback(Trigger = "floodvideo", GroupAdminOnly = true)]
        public static void floodvideo(CallbackQuery call, string[] args) => ToggleFloodException(call, args, "video");

        [Callback(Trigger = "floodgif", GroupAdminOnly = true)]
        public static void floodgif(CallbackQuery call, string[] args) => ToggleFloodException(call, args, "gif");

        /// <summary>
        /// Counts <paramref name="type"/> towards flood, or stops counting it. Text the group never set
        /// was shown as not counted and starts counted when pressed, as it always did.
        /// </summary>
        private static void ToggleFloodException(CallbackQuery call, string[] args, string type)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            ChangeDetection(call, chatId, lang, p =>
            {
                var counted = new Dictionary<string, bool>(p.Flood.Counted);
                counted[type] = !(counted.TryGetValue(type, out var current) && current);
                return p with { Flood = p.Flood with { Counted = counted } };
            }, Commands.genFlood);
        }
    }
}
