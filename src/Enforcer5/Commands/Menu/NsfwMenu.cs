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
        /// <summary>What a member whose profile shows nudity can get, in the menu's order.</summary>
        internal static readonly string[] NsfwProfileActions = { "mute", "ban", "report" };

        /// <summary>The NSFW screen (Daniel, 2026-11-05; <see cref="NsfwSigns"/>): images and stickers, and new members' profiles.</summary>
        internal static string NsfwMenuText(XDocument lang) => Methods.GetLocaleString(lang, "nsfwImagesMenu");

        internal static InlineKeyboardMarkup GenNsfwMenu(long chatId, XDocument lang)
        {
            var engine = DetectionSettings.For(chatId).Engine;
            var images = engine.For(SignCatalog.ImageNsfw.Name);
            var profile = engine.For(SignCatalog.ProfileNsfw.Name);
            var menu = new Menu(2);
            void Row(string label, string value, bool? on, string what)
            {
                menu.Buttons.Add(MenuInfo.Label(Methods.GetLocaleString(lang, label), chatId, label.Replace("Button", "")));
                menu.Buttons.Add(new InlineButton(value, $"nsfwtoggle:{chatId}:{what}") { Style = on == true ? KeyboardButtonStyle.Success : null });
            }
            string OnOff(bool on) => Methods.GetLocaleString(lang, on ? "on" : "off");
            Row("nsfwImagesButton", OnOff(images.Enabled), images.Enabled, "images");
            Row("nsfwProfileButton", OnOff(profile.Enabled), profile.Enabled, "profile");
            if (profile.Enabled)
                Row("nsfwProfileActionButton", Methods.GetLocaleString(lang, $"filterAction_{profile.Action}"), null, "action");
            var back = new Menu(1);
            back.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "backButton"), $"detectionmenu:{chatId}"));
            return Key.CreateMarkupFromMenus(menu, back);
        }
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "nsfwmenu", GroupAdminOnly = true)]
        public static void NsfwMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, Commands.NsfwMenuText(lang), ParseMode.Html,
                replyMarkup: Commands.GenNsfwMenu(chatId, lang)).Forget();
        }

        [Callback(Trigger = "nsfwtoggle", GroupAdminOnly = true)]
        public static void NsfwToggle(CallbackQuery call, string[] args)
        {
            if (args.Length < 3) return;
            var chatId = long.Parse(args[1]);
            var (sign, change) = args[2] switch
            {
                "images" => (SignCatalog.ImageNsfw.Name, (Func<SignPolicy, SignPolicy>)(s => s with { Enabled = !s.Enabled })),
                "profile" => (SignCatalog.ProfileNsfw.Name, s => s with { Enabled = !s.Enabled }),
                "action" => (SignCatalog.ProfileNsfw.Name, s => s with { Action = Next(s.Action, Commands.NsfwProfileActions) }),
                _ => (null, null)
            };
            if (change == null) return;
            ChangeDetection(call, chatId, Methods.GetGroupLanguage(chatId).Doc, p => p with
            {
                Engine = p.Engine with
                {
                    Signs = new Dictionary<string, SignPolicy>(p.Engine.Signs) { [sign] = change(p.Engine.For(sign)) }
                }
            }, Commands.GenNsfwMenu);
        }
    }
}
