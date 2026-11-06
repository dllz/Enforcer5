using System;
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
        /// <summary>The AI screen (build step 15; <see cref="SimilarToBanned"/>): messages and images like removed ones, each off, watching or live.</summary>
        internal static string AiMenuText(XDocument lang) => Methods.GetLocaleString(lang, "aiMenu");

        internal static InlineKeyboardMarkup GenAiMenu(long chatId, XDocument lang)
        {
            var engine = DetectionSettings.For(chatId).Engine;
            var menu = new Menu(2);
            void Row(string label, string mode, string what)
            {
                // A group set live while live is no longer offered is watching (SimilarToBanned.Mode).
                if (mode == AiModes.Live && !SimilarToBanned.LiveAllowed) mode = AiModes.Watch;
                menu.Buttons.Add(MenuInfo.Label(Methods.GetLocaleString(lang, label), chatId, label.Replace("Button", "")));
                menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, mode == AiModes.Off ? "off" : $"aiMode_{mode}"),
                    $"aitoggle:{chatId}:{what}") { Style = mode == AiModes.Live ? KeyboardButtonStyle.Success : null });
            }
            Row("aiTextButton", engine.TextSimilarity, "text");
            Row("aiImageButton", engine.ImageSimilarity, "image");
            var back = new Menu(1);
            back.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "backButton"), $"detectionmenu:{chatId}"));
            return Key.CreateMarkupFromMenus(menu, back);
        }
    }

    public static partial class CallBacks
    {
        [Callback(Trigger = "aimenu", GroupAdminOnly = true)]
        public static void AiMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, Commands.AiMenuText(lang), ParseMode.Html,
                replyMarkup: Commands.GenAiMenu(chatId, lang)).Forget();
        }

        [Callback(Trigger = "aitoggle", GroupAdminOnly = true)]
        public static void AiToggle(CallbackQuery call, string[] args)
        {
            if (args.Length < 3 || args[2] is not ("text" or "image")) return;
            var chatId = long.Parse(args[1]);
            var live = SimilarToBanned.LiveAllowed;
            ChangeDetection(call, chatId, Methods.GetGroupLanguage(chatId).Doc, p => p with
            {
                Engine = args[2] == "text"
                    ? p.Engine with { AiText = AiModes.Next(p.Engine.TextSimilarity, live) }
                    : p.Engine with { AiImage = AiModes.Next(p.Engine.ImageSimilarity, live) }
            }, Commands.GenAiMenu);
        }
    }
}
