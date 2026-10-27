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
        public static InlineKeyboardMarkup genAntiLengthMenu(long chatId, XDocument lang)
        {
            var policy = Detection.DetectionSettings.For(chatId);
            var name = policy.NameLength;
            var text = policy.TextLength;

            var menu = new Menu(2);
            menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "nameSettingsHeader"), "null"));
            menu.Buttons.Add(new InlineButton($"{name.MaxCharacters ?? OnMessage.DefaultMaxNameLength}", "null"));
            AddLengthButtons(menu, lang, name, $"namesettings:{chatId}", $"namesettingsaction:{chatId}");
            menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "textSettingsHeader"), "null"));
            menu.Buttons.Add(new InlineButton(
                $"{text.MaxCharacters ?? OnMessage.DefaultMaxTextLength} : {text.MaxLines ?? OnMessage.DefaultMaxTextLines}", "null"));
            AddLengthButtons(menu, lang, text, $"textsettings:{chatId}", $"textsettingsaction:{chatId}");
            var close = new Menu(1);
            close.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "backButton"), $"back:{chatId}"));
            return Key.CreateMarkupFromMenus(menu, close);
        }

        private static void AddLengthButtons(Menu menu, XDocument lang, Detection.LengthSettings length, string toggle, string cycle)
        {
            menu.Buttons.Add(length.Enabled
                ? new InlineButton($"✅ | {Methods.GetLocaleString(lang, "on")}", toggle)
                : new InlineButton($"❌ | {Methods.GetLocaleString(lang, "off")}", toggle));
            switch (length.Action)
            {
                case "kick":
                    menu.Buttons.Add(new InlineButton($"⚡️ | {Methods.GetLocaleString(lang, "kick")}", cycle));
                    break;
                case "ban":
                    menu.Buttons.Add(new InlineButton($"⛔ | {Methods.GetLocaleString(lang, "ban")}", cycle));
                    break;
                case "warn":
                    menu.Buttons.Add(new InlineButton($"⚠️ | {Methods.GetLocaleString(lang, "Warn")}", cycle));
                    break;
                case "tempban":
                    menu.Buttons.Add(new InlineButton($"⏳ | {Methods.GetLocaleString(lang, "tempban")}", cycle));
                    break;
            }
        }
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "openLengthMenu")]
        public static void openLengthMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId);
            var text = Methods.GetLocaleString(lang.Doc, "lengthMenu", lang.Base);
            var keys = Commands.genAntiLengthMenu(chatId, lang.Doc);
            Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, text, replyMarkup:keys);
        }

        private static readonly string[] LengthActions = { "kick", "warn", "tempban", "ban" };

        [Callback(Trigger = "namesettingsaction", GroupAdminOnly = true)]
        public static void NameSettingsAction(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            ChangeDetection(call, chatId, lang,
                p => p with { NameLength = p.NameLength with { Action = Next(p.NameLength.Action, LengthActions) } },
                Commands.genAntiLengthMenu);
        }

        [Callback(Trigger = "namesettings", GroupAdminOnly = true)]
        public static void NameSettings(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            ChangeDetection(call, chatId, lang,
                p => p with { NameLength = p.NameLength with { Enabled = !p.NameLength.Enabled } },
                Commands.genAntiLengthMenu);
        }

        [Callback(Trigger = "textsettingsaction", GroupAdminOnly = true)]
        public static void TextSettingsAction(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            ChangeDetection(call, chatId, lang,
                p => p with { TextLength = p.TextLength with { Action = Next(p.TextLength.Action, LengthActions) } },
                Commands.genAntiLengthMenu);
        }

        [Callback(Trigger = "textsettings", GroupAdminOnly = true)]
        public static void TextSettings(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            ChangeDetection(call, chatId, lang,
                p => p with { TextLength = p.TextLength with { Enabled = !p.TextLength.Enabled } },
                Commands.genAntiLengthMenu);
        }
    }
}
