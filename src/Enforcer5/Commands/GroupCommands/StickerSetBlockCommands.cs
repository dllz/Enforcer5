using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Detection;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5
{
    /// <summary>
    /// /stickerinfo: a sticker or custom emoji pack's name and link, and whether it is blocked (/block). Enforcement lives in
    /// OnMessage.BlockedStickerSet and <see cref="EmojiCheck"/>.
    /// </summary>
    public static partial class Commands
    {
        /// <summary>
        /// Shows a pack's name, so it can be blocked. Works in a private chat for anyone, which is
        /// how an admin finds the name without posting the sticker in the group. In a group it is
        /// admin-only and also says whether the pack is blocked there. That check is made here
        /// rather than with GroupAdminOnly, which would also refuse it in private chats.
        /// </summary>
        [Command(Trigger = "stickerinfo")]
        public static void StickerInfo(Update update, string[] args)
        {
            var message = update.Message;
            var lang = Methods.GetGroupLanguage(message, true).Doc;
            var inGroup = message.Chat.Type != ChatType.Private;
            if (inGroup && !Methods.IsGroupAdmin(update) && !Methods.IsGlobalAdmin(message.From.Id) &&
                !Constants.Devs.Contains(message.From.Id))
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "userNotAdmin"), update);
                return;
            }
            if (!TryReadStickerSet(update, args, lang, out var name, out var fromReply)) return;

            var set = GetStickerSet(name, out var notFound);
            if (notFound && !fromReply)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "stickerSetNotFound", name), update);
                return;
            }

            bool? blocked = null;
            if (inGroup)
            {
                var policy = DetectionSettings.GetAsync(message.Chat.Id).GetAwaiter().GetResult();
                blocked = policy.StickerSets.Contains(name, StringComparer.Ordinal) || policy.EmojiSetBlocked(name);
            }
            Bot.SendReply(StickerInfoReply(lang, name, set, blocked), update);
        }

        /// <summary>
        /// The /stickerinfo answer. <paramref name="set"/> is null when Telegram's details could not
        /// be fetched; <paramref name="blocked"/> is null in a private chat, where there is no group
        /// to check. Every argument goes through GetLocaleString, which escapes it for HTML.
        /// </summary>
        internal static string StickerInfoReply(XDocument lang, string name, StickerSet set, bool? blocked)
        {
            var shownName = set?.Name ?? name;
            var emoji = set?.StickerType == StickerType.CustomEmoji;
            var lines = new List<string>();
            if (!string.IsNullOrEmpty(set?.Title))
                lines.Add(Methods.GetLocaleString(lang, "stickerInfoTitle", set.Title));
            lines.Add(Methods.GetLocaleString(lang, "stickerInfoName", shownName));
            lines.Add(Methods.GetLocaleString(lang, "stickerInfoLink", StickerSetName.Link(shownName, emoji)));
            if (set?.Stickers != null)
                lines.Add(Methods.GetLocaleString(lang, "stickerInfoCount", set.Stickers.Length));

            if (blocked == true)
                lines.Add(Methods.GetLocaleString(lang, "packInfoBlocked", name));
            else if (blocked == false)
                lines.Add(Methods.GetLocaleString(lang, "packInfoNotBlocked", name));
            else
                lines.Add(Methods.GetLocaleString(lang, "packInfoHowToBlock", name));
            return string.Join("\n", lines);
        }

        /// <summary>
        /// Fetches the pack from Telegram. Null if that failed; <paramref name="notFound"/> is set
        /// when Telegram said the pack does not exist, as opposed to the call failing.
        /// </summary>
        private static StickerSet GetStickerSet(string name, out bool notFound)
        {
            notFound = false;
            try
            {
                return Bot.Api.GetStickerSet(name).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                var api = Bot.AsApiError(e);
                if (api?.ErrorCode == 400)
                {
                    // Telegram says the pack does not exist: a typo, handled by the caller.
                    notFound = true;
                }
                else
                {
                    ErrorReporter.Report(e, $"Sticker set lookup for {name}");
                }
                return null;
            }
        }

        /// <summary>
        /// Reads the pack from the command argument (a name or link) or, failing that, from the
        /// replied-to sticker or the first custom emoji of the replied-to message. Replies with the
        /// reason and returns false if there is no valid pack.
        /// </summary>
        private static bool TryReadStickerSet(Update update, string[] args, XDocument lang, out string name, out bool fromReply)
        {
            fromReply = false;
            var input = args.Length > 1 ? args[1] : null;
            if (!string.IsNullOrWhiteSpace(input))
            {
                name = StickerSetName.Parse(input);
                if (name != null) return true;
                Bot.SendReply(Methods.GetLocaleString(lang, "stickerSetInvalidName", input.Trim()), update);
                return false;
            }

            var replied = update.Message.ReplyToMessage;
            var sticker = replied?.Sticker;
            if (sticker != null)
            {
                name = StickerSetName.Normalise(sticker.SetName);
                fromReply = name != null;
                if (fromReply) return true;
                Bot.SendReply(Methods.GetLocaleString(lang, "stickerNoSet"), update);
                return false;
            }

            var ids = CustomEmojis.IdsOf(replied);
            if (ids.Count > 0)
            {
                var emoji = CustomEmojis.ResolveAsync(ids).GetAwaiter().GetResult();
                name = ids.Select(id => emoji.TryGetValue(id, out var e) ? e.SetName : null).FirstOrDefault(n => n != null);
                fromReply = name != null;
                if (fromReply) return true;
                Bot.SendReply(Methods.GetLocaleString(lang, "stickerNoSet"), update);
                return false;
            }

            name = null;
            Bot.SendReply(Methods.GetLocaleString(lang, "stickerBlockUsage"), update);
            return false;
        }
    }
}
