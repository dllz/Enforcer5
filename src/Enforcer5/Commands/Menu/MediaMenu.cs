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

namespace Enforcer5
{
    public static partial class Commands
    {
        public static InlineKeyboardMarkup genMediaMenu(long chatId, XDocument lang)
        {
            var media = Detection.DetectionSettings.For(chatId).Media;
            var menu = new Menu(2);
            // Text only when the group has set it: it has a button, but new groups never got it.
            var types = media.Blocked.ContainsKey("text")
                ? Detection.MediaSettings.MenuTypes.Append("text")
                : Detection.MediaSettings.MenuTypes.AsEnumerable();
            foreach (var type in types)
            {
                menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, $"{type}Button"),
                    $"mediasettings:{type}"));
                menu.Buttons.Add(new InlineButton(media.IsBlocked(type) ? "❌" : "✅", $"media{type}:{chatId}"));
            }
            var warnTitle = new Menu(1);
            switch (media.Action)
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
            editWarn.Buttons.Add(new InlineButton($"📍 {media.EffectiveWarnLimit}", $"mediaActionWarn:{chatId}"));
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
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            ChangeDetection(call, chatId, lang,
                p => p with { Media = p.Media with { Action = Next(p.Media.Action, "kick", "tempban", "ban") } },
                Commands.genMediaMenu);
        }

        [Callback(Trigger = "mediaDimWarn", GroupAdminOnly = true)]
        public static void mediaDimWarn(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            if (Detection.DetectionSettings.For(chatId).Media.EffectiveWarnLimit <= 1)
            {
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "warnToLow"));
                return;
            }
            ChangeDetection(call, chatId, lang,
                p => p with { Media = p.Media with { WarnLimit = Math.Max(1, p.Media.EffectiveWarnLimit - 1) } },
                Commands.genMediaMenu);
        }

        [Callback(Trigger = "mediaRaiseWarn", GroupAdminOnly = true)]
        public static void mediaRaiseWarn(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            ChangeDetection(call, chatId, lang,
                p => p with { Media = p.Media with { WarnLimit = p.Media.EffectiveWarnLimit + 1 } },
                Commands.genMediaMenu);
        }

        [Callback(Trigger = "mediatext", GroupAdminOnly = true)]
        public static void mediatext(CallbackQuery call, string[] args) => ToggleMedia(call, args, "text");

        [Callback(Trigger = "mediasticker", GroupAdminOnly = true)]
        public static void mediasticker(CallbackQuery call, string[] args) => ToggleMedia(call, args, "sticker");

        [Callback(Trigger = "mediaimage", GroupAdminOnly = true)]
        public static void mediaimage(CallbackQuery call, string[] args) => ToggleMedia(call, args, "image");

        [Callback(Trigger = "mediavideo", GroupAdminOnly = true)]
        public static void mediavideo(CallbackQuery call, string[] args) => ToggleMedia(call, args, "video");

        [Callback(Trigger = "mediagif", GroupAdminOnly = true)]
        public static void mediagif(CallbackQuery call, string[] args) => ToggleMedia(call, args, "gif");

        [Callback(Trigger = "mediacontact", GroupAdminOnly = true)]
        public static void mediacontact(CallbackQuery call, string[] args) => ToggleMedia(call, args, "contact");

        [Callback(Trigger = "mediafile", GroupAdminOnly = true)]
        public static void mediafile(CallbackQuery call, string[] args) => ToggleMedia(call, args, "file");

        [Callback(Trigger = "medialink", GroupAdminOnly = true)]
        public static void medialink(CallbackQuery call, string[] args) => ToggleMedia(call, args, "link");

        [Callback(Trigger = "mediavoice", GroupAdminOnly = true)]
        public static void mediavoice(CallbackQuery call, string[] args) => ToggleMedia(call, args, "voice");

        [Callback(Trigger = "mediaaudio", GroupAdminOnly = true)]
        public static void mediaaudio(CallbackQuery call, string[] args) => ToggleMedia(call, args, "audio");

        /// <summary>Blocks <paramref name="type"/>, or allows it again.</summary>
        private static void ToggleMedia(CallbackQuery call, string[] args, string type)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            ChangeDetection(call, chatId, lang, p =>
            {
                var blocked = new Dictionary<string, bool>(p.Media.Blocked) { [type] = !p.Media.IsBlocked(type) };
                return p with { Media = p.Media with { Blocked = blocked } };
            }, Commands.genMediaMenu);
        }
    }
}
