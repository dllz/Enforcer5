using System;
using System.Linq;
using System.Security.Cryptography;
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
        /// <summary>How long a "link admin chat" button works (design section 6).</summary>
        internal static readonly TimeSpan AdminChatLinkFor = TimeSpan.FromMinutes(10);

        internal const string AdminChatStart = "adminchat_";

        internal static string AdminChatTokenKey(string token) => $"detect:adminchat:{token}";

        /// <summary>
        /// The Reports screen (design section 6, build step 7): where the group's Detection reports go, its
        /// admin chat, and whether the admin looking gets them in private.
        /// </summary>
        internal static string ReportsMenuText(long chatId, long adminId, XDocument lang)
        {
            var engine = DetectionSettings.For(chatId).Engine;
            var adminChat = engine.AdminChatId is { } linked
                ? ChatDetails.GetAsync(linked).GetAwaiter().GetResult()?.Title ?? linked.ToString()
                : Methods.GetLocaleString(lang, "reportsNoAdminChat");
            var optedOut = DetectionReports.OptedOutAsync(chatId).GetAwaiter().GetResult().Contains(adminId);
            return Methods.GetLocaleString(lang, "reportsMenu", Methods.GetLocaleString(lang, $"reportsTo_{engine.ReportTo}"), adminChat,
                Methods.GetLocaleString(lang, optedOut ? "reportsYouOff" : "reportsYouOn"));
        }

        internal static InlineKeyboardMarkup GenReportsMenu(long chatId, long adminId, XDocument lang)
        {
            var engine = DetectionSettings.For(chatId).Engine;
            var optedOut = DetectionReports.OptedOutAsync(chatId).GetAwaiter().GetResult().Contains(adminId);
            var menu = new Menu(1);
            menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "reportsToButton",
                Methods.GetLocaleString(lang, $"reportsTo_{engine.ReportTo}")), $"reportsto:{chatId}"));
            menu.Buttons.Add(engine.AdminChatId == null
                ? new InlineButton(Methods.GetLocaleString(lang, "reportsLinkButton"), $"reportslink:{chatId}") { Style = KeyboardButtonStyle.Primary }
                : new InlineButton(Methods.GetLocaleString(lang, "reportsUnlinkButton"), $"reportsunlink:{chatId}") { Style = KeyboardButtonStyle.Danger });
            menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "reportsMeButton",
                    Methods.GetLocaleString(lang, optedOut ? "off" : "on")), $"reportsme:{chatId}")
                { Style = optedOut ? null : KeyboardButtonStyle.Success });
            menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "backButton"), $"detectionmenu:{chatId}"));
            return Key.CreateMarkupFromMenu(menu);
        }

        /// <summary>
        /// /start adminchat_{token} in a group: the bot was added to (or picked in) a chat from a group's
        /// "Link admin chat" button. The token works once, for 10 minutes, and whoever sent it must be an
        /// admin of both chats.
        /// </summary>
        internal static void LinkAdminChat(Message message, string token)
        {
            var chat = message.Chat.Id;
            var lang = Methods.GetGroupLanguage(message, true).Doc;
            var key = AdminChatTokenKey(token);
            var stored = Redis.db.StringGetAsync(key).GetAwaiter().GetResult();
            var parts = stored.HasValue ? stored.ToString().Split(':') : Array.Empty<string>();
            if (parts.Length != 2 || !long.TryParse(parts[0], out var group))
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "adminChatLinkExpired"), message);
                return;
            }
            var from = message.From?.Id ?? 0;
            if (group == chat)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "adminChatSame"), message);
                return;
            }
            if (!Methods.IsGroupAdmin(from, group) || !Methods.IsGroupAdmin(from, chat))
            {
                // The link stays usable: whoever tried may just have been someone else in the chat.
                Bot.SendReply(Methods.GetLocaleString(lang, "adminChatNotAdmin"), message);
                return;
            }
            // Once only: of two uses at the same moment, one deletes it.
            if (!Redis.db.KeyDeleteAsync(key).GetAwaiter().GetResult())
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "adminChatLinkExpired"), message);
                return;
            }
            var saved = DetectionSettings.TryUpdate(group, p => p with
            {
                Engine = p.Engine with
                {
                    AdminChatId = chat,
                    // Linking a chat is asking for the reports there.
                    ReportTo = p.Engine.ReportTo == EngineSettings.ToAdmins ? EngineSettings.ToAdminChat : p.Engine.ReportTo
                }
            });
            if (saved == null)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "failed"), message);
                return;
            }
            var title = ChatDetails.GetAsync(group).GetAwaiter().GetResult()?.Title ?? group.ToString();
            Bot.SendReply(Methods.GetLocaleString(lang, "adminChatLinked", title), message);
            Service.LogCommand(group, from, message.From?.FirstName, title, $"/start {AdminChatStart}", chat.ToString());
        }
    }

    public static partial class CallBacks
    {
        private static void ShowReportsMenu(CallbackQuery call, long chatId, XDocument lang) =>
            Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId, Commands.ReportsMenuText(chatId, call.From.Id, lang),
                ParseMode.Html, replyMarkup: Commands.GenReportsMenu(chatId, call.From.Id, lang)).GetAwaiter().GetResult();

        [Callback(Trigger = "reportsmenu", GroupAdminOnly = true)]
        public static void ReportsMenu(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            ShowReportsMenu(call, chatId, Methods.GetGroupLanguage(chatId).Doc);
        }

        /// <summary>Admins in private, the admin chat, or both.</summary>
        [Callback(Trigger = "reportsto", GroupAdminOnly = true)]
        public static void ReportsTo(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            if (DetectionSettings.TryUpdate(chatId, p => p with
                {
                    Engine = p.Engine with { ReportTo = Next(p.Engine.ReportTo, EngineSettings.ReportChoices) }
                }) == null)
            {
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "failed"));
                return;
            }
            Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            ShowReportsMenu(call, chatId, lang);
        }

        /// <summary>The admin pressing it gets the group's reports in private, or not.</summary>
        [Callback(Trigger = "reportsme", GroupAdminOnly = true)]
        public static void ReportsMe(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var optedOut = DetectionReports.OptedOutAsync(chatId).GetAwaiter().GetResult().Contains(call.From.Id);
            if (!DetectionReports.SetOptedOut(chatId, call.From.Id, !optedOut))
            {
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "failed"));
                return;
            }
            Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            ShowReportsMenu(call, chatId, lang);
        }

        /// <summary>A one-time link that adds the bot to the admin chat, or picks it if the bot is there.</summary>
        [Callback(Trigger = "reportslink", GroupAdminOnly = true)]
        public static void ReportsLink(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
            Redis.db.StringSetAsync(Commands.AdminChatTokenKey(token), $"{chatId}:{call.From.Id}", Commands.AdminChatLinkFor)
                .GetAwaiter().GetResult();
            var menu = new Menu(1);
            menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "reportsPickChatButton"))
            {
                Url = $"https://t.me/{Bot.Me.Username}?startgroup={Commands.AdminChatStart}{token}",
                Style = KeyboardButtonStyle.Primary
            });
            menu.Buttons.Add(new InlineButton(Methods.GetLocaleString(lang, "backButton"), $"reportsmenu:{chatId}"));
            Bot.Api.EditMessageText(call.From.Id, call.Message.MessageId,
                Methods.GetLocaleString(lang, "reportsLinkText", (int)Commands.AdminChatLinkFor.TotalMinutes),
                replyMarkup: Key.CreateMarkupFromMenu(menu)).GetAwaiter().GetResult();
        }

        [Callback(Trigger = "reportsunlink", GroupAdminOnly = true)]
        public static void ReportsUnlink(CallbackQuery call, string[] args)
        {
            var chatId = long.Parse(args[1]);
            var lang = Methods.GetGroupLanguage(chatId).Doc;
            if (DetectionSettings.TryUpdate(chatId, p => p with { Engine = p.Engine with { AdminChatId = null } }) == null)
            {
                Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "failed"));
                return;
            }
            Bot.Api.AnswerCallbackQuery(call.Id, Methods.GetLocaleString(lang, "settingChanged"));
            ShowReportsMenu(call, chatId, lang);
        }

        /// <summary>Ban, Mute or Fine under a Detection report: dreport:{chat}:{user}:{opened}:{decision}.</summary>
        [Callback(Trigger = DetectionReports.Trigger, GroupAdminOnly = true)]
        public static void DetectionReportButton(CallbackQuery call, string[] args)
        {
            if (args.Length < 5 || !long.TryParse(args[2], out var userId) || !long.TryParse(args[3], out var opened) ||
                args[4] is not ("ban" or "mute" or "fine")) return;
            DetectionReports.PressAsync(call, long.Parse(args[1]), userId, opened, args[4]).GetAwaiter().GetResult();
        }

        /// <summary>Make live, 7 more days or Delete under a filter's end-of-trial notice: ftrial:{chat}:{filter}:{sent}:{decision}.</summary>
        [Callback(Trigger = FilterTrials.Trigger, GroupAdminOnly = true)]
        public static void FilterTrialButton(CallbackQuery call, string[] args)
        {
            if (args.Length < 5 || !long.TryParse(args[2], out var filterId) || !long.TryParse(args[3], out var sent) ||
                args[4] is not ("live" or "extend" or "delete")) return;
            FilterTrials.PressAsync(call, long.Parse(args[1]), filterId, sent, args[4]).GetAwaiter().GetResult();
        }
    }
}
