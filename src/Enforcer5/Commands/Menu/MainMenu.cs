using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5
{
    /// <summary>
    /// The settings menu, always in the admin's private chat with the bot, one message edited as they move
    /// through it. Reached three ways:
    /// <list type="bullet">
    /// <item>/menu in a group: sent in private; when the admin has not started the bot, the group gets a
    /// button that starts it and opens the menu in one tap (deep link <c>menu_{chat}</c>).</item>
    /// <item>/menu in private: a "Choose a group" keyboard button (Telegram's chat picker, which only lists
    /// groups the admin runs and the bot is in).</item>
    /// <item>/start menu_{chat}: from that deep link.</item>
    /// </list>
    /// Every screen says what it is for, buttons are coloured by what they do (Telegram button styles), and
    /// every sub-menu has Back; Done removes the menu.
    /// </summary>
    public static partial class Commands
    {
        /// <summary>The chat picker's request id; its answer comes back as a ChatShared message.</summary>
        internal const int MenuChatRequest = 1;

        [Command(Trigger = "menu", Menu = Attributes.CommandMenu.GroupAdmins)]
        public static void Menu(Update update, string[] args)
        {
            var message = update.Message;
            if (message.Chat.Type == ChatType.Private)
            {
                AskForGroup(message.From.Id, Methods.GetGroupLanguage(message, false).Doc);
                return;
            }
            var lang = Methods.GetGroupLanguage(message, false).Doc;
            var chatId = message.Chat.Id;
            if (!Methods.IsGroupAdmin(update))
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "userNotAdmin"), update);
                return;
            }
            if (!Redis.db.HashGet($"chat:{chatId}:settings", "DeleteLastWelcome").HasValue)
                tempAddDeleteLastWelcomeSetting(update, chatId);
            Service.LogCommand(update, message.Text);
            if (SendMainMenu(message.From.Id, chatId, message.Chat.Title) != null)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "botPm"), update);
                return;
            }
            // Not started (or blocked): one tap starts the bot and opens the menu.
            var open = new Menu(1);
            open.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "menuOpenInPrivate"),
                url: $"https://t.me/{Bot.Me.Username}?start=menu_{chatId}") { Style = KeyboardButtonStyle.Primary });
            Bot.SendReply(Methods.GetLocaleString(lang, "menuStartFirst"), message.Chat.Id, message.MessageId, Key.CreateMarkupFromMenu(open));
        }

        /// <summary>/menu in private: Telegram's own picker of the groups the admin runs and the bot is in.</summary>
        internal static void AskForGroup(long userId, XDocument lang)
        {
            var pick = new ReplyKeyboardMarkup(new KeyboardButton(Methods.GetLocaleString(lang, "menuChooseGroup"))
            {
                RequestChat = new KeyboardButtonRequestChat
                {
                    RequestId = MenuChatRequest,
                    ChatIsChannel = false,
                    BotIsMember = true,
                    UserAdministratorRights = new ChatAdministratorRights(),
                    RequestTitle = true
                },
                Style = KeyboardButtonStyle.Primary
            })
            {
                ResizeKeyboard = true,
                OneTimeKeyboard = true
            };
            Bot.Api.SendMessage(userId, Methods.GetLocaleString(lang, "menuChooseGroupText"), ParseMode.Html, replyMarkup: pick)
                .GetAwaiter().GetResult();
        }

        /// <summary>The admin picked a group: its menu, if they still run it.</summary>
        internal static void MenuForSharedChat(Message message)
        {
            var shared = message.ChatShared;
            if (shared == null || shared.RequestId != MenuChatRequest) return;
            var userId = message.From.Id;
            var lang = Methods.GetGroupLanguage(shared.ChatId).Doc;
            // Telegram only lists groups where they are an admin, but that can change before they tap.
            var allowed = Methods.IsGroupAdmin(userId, shared.ChatId);
            Bot.Api.SendMessage(userId,
                Methods.GetLocaleString(lang, allowed ? "menuOpening" : "userNotAdmin", shared.Title ?? ""),
                ParseMode.Html, replyMarkup: new ReplyKeyboardRemove()).GetAwaiter().GetResult();
            if (allowed) SendMainMenu(userId, shared.ChatId, shared.Title);
        }

        /// <summary>/start menu_{chat}.</summary>
        internal static void MenuFromStart(Message message, long chatId)
        {
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            if (!Methods.IsGroupAdmin(message.From.Id, chatId))
            {
                Bot.Send(Methods.GetLocaleString(lang, "userNotAdmin"), message.From.Id);
                return;
            }
            SendMainMenu(message.From.Id, chatId, null);
        }

        /// <summary>Sends the main menu in private; null when the admin cannot be reached there.</summary>
        internal static Message SendMainMenu(long userId, long chatId, string title)
        {
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            try
            {
                return Bot.Api.SendMessage(userId, MainMenuText(chatId, lang, title), ParseMode.Html,
                    replyMarkup: genMenu(chatId, lang)).GetAwaiter().GetResult();
            }
            catch (Exception e) when (Bot.AsApiError(e)?.ErrorCode is 400 or 403)
            {
                // Expected: they never started the bot, or blocked it. The caller offers the deep link.
                return null;
            }
        }

        internal static string MainMenuText(long chatId, XDocument lang, string title = null)
        {
            title ??= GroupTitle(chatId);
            return Methods.GetLocaleString(lang, "settingsHome", title);
        }

        /// <summary>The group's title for a menu header; empty when Telegram does not say.</summary>
        internal static string GroupTitle(long chatId)
        {
            try
            {
                return ChatDetails.GetAsync(chatId).GetAwaiter().GetResult()?.Title ?? "";
            }
            catch (Exception e)
            {
                // A header without the title is fine; the menu still works.
                LogHelper.Info($"No title for the menu of {chatId}: {e.GetBaseException().Message}");
                return "";
            }
        }

        public static InlineKeyboardMarkup genMenu(long chatId, XDocument lang)
        {
            var settings = Redis.db.HashGetAllAsync($"chat:{chatId}:settings").Result;
            // A group whose settings were never generated. Flood and Mute On Join left this hash
            // for PostgreSQL (Detection step 2), so a generated one has 8 fields now, not 9.
            if (settings.Length < 8) Service.GenerateSettings(chatId);

            InlineButton Button(string key, string trigger) =>
                new InlineButton(Methods.GetLocaleString(lang, key), $"{trigger}:{chatId}");
            var detection = new Menu(1);
            detection.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "detectionButton"), $"detectionmenu:{chatId}")
            {
                Style = KeyboardButtonStyle.Primary
            });
            var sections = new Menu(2);
            sections.Buttons.Add(Button("menuFloodButton", "openFloodMenu"));
            sections.Buttons.Add(Button("menuLengthButton", "openLengthMenu"));
            sections.Buttons.Add(Button("menuMediaButton", "openMediaMenu"));
            sections.Buttons.Add(Button("menuWarnButton", "openWarnMenu"));
            sections.Buttons.Add(Button("menuGroupButton", "openGroupMenu"));
            sections.Buttons.Add(Button("menuLanguageButton", "openLangMenu"));
            var close = new Menu(1);
            close.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "closeButton"), "close"));
            return Key.CreateMarkupFromMenus(detection, sections, close);
        }
    }

    public static partial class CallBacks
    {
        /// <summary>Done: the menu goes, rather than staying behind as "Good Bye".</summary>
        [Callback(Trigger = "close")]
        public static void CloseButton(CallbackQuery call, string[] args)
        {
            try
            {
                Bot.Api.DeleteMessage(call.Message.Chat.Id, call.Message.MessageId).GetAwaiter().GetResult();
            }
            catch (Exception e) when (Bot.AsApiError(e)?.ErrorCode == 400)
            {
                // Older than 48 hours, which a bot may not delete: say goodbye instead.
                Bot.Api.EditMessageText(call.Message.Chat.Id, call.Message.MessageId, "👋").GetAwaiter().GetResult();
            }
        }

        [Callback(Trigger = "back", GroupAdminOnly = true)]
        public static void BackTaskButton(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, Commands.MainMenuText(chatId, lang),
                ParseMode.Html, replyMarkup: Commands.genMenu(chatId, lang));
        }
    }
}
