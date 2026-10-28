using System;
using System.Collections.Generic;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Detection;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5
{
    public static partial class Commands
    {
        /// <summary>The Edits screen (Detection build step 8): swapped media always goes; what late edits get is the group's choice.</summary>
        internal static string EditsMenuText(XDocument lang) =>
            Methods.GetLocaleString(lang, "editsMenu", (int)EditCheck.LateAfter.TotalMinutes, (int)EditCheck.Window.TotalDays);

        internal static InlineKeyboardMarkup GenEditsMenu(long chatId, XDocument lang)
        {
            var late = DetectionSettings.For(chatId).Engine.For(SignCatalog.EditLate.Name);
            var menu = new Menu(2);
            void Toggle(string label, bool on, string what)
            {
                menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, label), $"editstoggle:{chatId}:{what}"));
                menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, on ? "on" : "off"), $"editstoggle:{chatId}:{what}")
                    { Style = on ? KeyboardButtonStyle.Success : null });
            }
            Toggle("editsLateButton", late.Enabled, "late");
            if (late.Enabled)
            {
                Toggle("editsDeleteNewButton", late.DeleteNewcomers, "deletenew");
                Toggle("editsEstablishedButton", !late.SkipEstablished, "established");
            }
            var back = new Menu(1);
            back.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "backButton"), $"detectionmenu:{chatId}"));
            return Key.CreateMarkupFromMenus(menu, back);
        }
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "editsmenu", GroupAdminOnly = true)]
        public static void EditsMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, Commands.EditsMenuText(lang), ParseMode.Html,
                replyMarkup: Commands.GenEditsMenu(chatId, lang));
        }

        [Callback(Trigger = "editstoggle", GroupAdminOnly = true)]
        public static void EditsToggle(CallbackQuery call, string[] args)
        {
            if (args.Length < 3) return;
            var chatId = long.Parse(args[1]);
            Func<SignPolicy, SignPolicy> change = args[2] switch
            {
                "late" => s => s with { Enabled = !s.Enabled },
                "deletenew" => s => s with { DeleteNewcomers = !s.DeleteNewcomers },
                "established" => s => s with { SkipEstablished = !s.SkipEstablished },
                _ => null
            };
            if (change == null) return;
            var sign = SignCatalog.EditLate.Name;
            ChangeDetection(call, chatId, Methods.GetGroupLanguage(chatId).Doc, p => p with
            {
                Engine = p.Engine with
                {
                    Signs = new Dictionary<string, SignPolicy>(p.Engine.Signs) { [sign] = change(p.Engine.For(sign)) }
                }
            }, Commands.GenEditsMenu);
        }
    }
}
