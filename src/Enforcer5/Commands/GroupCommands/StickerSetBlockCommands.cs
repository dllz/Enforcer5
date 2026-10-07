using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Enforcer5.Attributes;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5
{
    /// <summary>
    /// Admin commands for the per-chat sticker pack blocklist, and /stickerinfo to find a pack's
    /// name. Enforcement lives in OnMessage.BlockedStickerSet and storage behind
    /// <see cref="IStickerSetBlockRepository"/>.
    ///
    /// Only stickers are covered. Custom emoji inside text carry an emoji id rather than a pack
    /// name, and resolving it would need a Telegram call per message.
    /// </summary>
    public static partial class Commands
    {
        /// <summary>
        /// Lookups are one set-membership test whatever the size, so this only keeps the
        /// /blockedstickers listing inside a single Telegram message (64-character names).
        /// </summary>
        private const int MaxStickerSetBlocks = 50;

        [Command(Trigger = "blocksticker", InGroupOnly = true, GroupAdminOnly = true)]
        public static void BlockStickerSet(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            if (!TryReadStickerSet(update, args, lang, out var name, out var fromReply)) return;

            var chatId = update.Message.Chat.Id;
            var repository = Repositories.StickerSetBlocks;
            if (repository.CountAsync(chatId).GetAwaiter().GetResult() >= MaxStickerSetBlocks)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "stickerBlockLimit", MaxStickerSetBlocks), update);
                return;
            }

            // A typed name is checked against Telegram, since a typo would block nothing. A name
            // taken from a sticker came from Telegram, so it is blocked even if the lookup fails.
            var set = GetStickerSet(name, out var notFound);
            if (notFound && !fromReply)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "stickerSetNotFound", name), update);
                return;
            }
            if (set?.StickerType == StickerType.CustomEmoji)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "stickerSetIsEmoji", StickerSetLabel(name, set)), update);
                return;
            }

            var added = repository.AddAsync(chatId, name).GetAwaiter().GetResult();
            Bot.SendReply(Methods.GetLocaleString(lang, added ? "stickerBlockAdded" : "stickerBlockExists",
                StickerSetLabel(name, set)), update);
            if (added) Service.LogCommand(update, update.Message.Text);
        }

        [Command(Trigger = "unblocksticker", InGroupOnly = true, GroupAdminOnly = true)]
        public static void UnblockStickerSet(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            // No Telegram lookup: a pack that has since been deleted must still be removable.
            if (!TryReadStickerSet(update, args, lang, out var name, out _)) return;

            var removed = Repositories.StickerSetBlocks.RemoveAsync(update.Message.Chat.Id, name).GetAwaiter().GetResult();
            Bot.SendReply(Methods.GetLocaleString(lang, removed ? "stickerBlockRemoved" : "stickerBlockNotFound", name), update);
            if (removed) Service.LogCommand(update, update.Message.Text);
        }

        // Admin-only, like /blockedinline.
        [Command(Trigger = "blockedstickers", InGroupOnly = true, GroupAdminOnly = true)]
        public static void BlockedStickerSets(Update update, string[] args)
        {
            var lang = Methods.GetGroupLanguage(update.Message, true).Doc;
            var names = Repositories.StickerSetBlocks.GetAsync(update.Message.Chat.Id).GetAwaiter().GetResult();
            if (names.Count == 0)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "stickerBlockListEmpty"), update);
                return;
            }

            var list = string.Join("\n", names.OrderBy(name => name, StringComparer.Ordinal));
            Bot.SendReply(Methods.GetLocaleString(lang, "stickerBlockList", list), update);
        }

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

            bool? blocked = inGroup
                ? Repositories.StickerSetBlocks.ContainsAsync(message.Chat.Id, name).GetAwaiter().GetResult()
                : null;
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

            if (emoji)
                lines.Add(Methods.GetLocaleString(lang, "stickerInfoEmojiPack"));
            else if (blocked == true)
                lines.Add(Methods.GetLocaleString(lang, "stickerInfoBlocked", name));
            else if (blocked == false)
                lines.Add(Methods.GetLocaleString(lang, "stickerInfoNotBlocked", name));
            else
                lines.Add(Methods.GetLocaleString(lang, "stickerInfoHowToBlock", name));
            return string.Join("\n", lines);
        }

        private static string StickerSetLabel(string name, StickerSet set) =>
            string.IsNullOrEmpty(set?.Title) ? name : $"{set.Title} ({name})";

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
                    notFound = true;
                }
                else
                {
                    LogHelper.Error($"Sticker set lookup for {name} failed: {api?.Message ?? e.Message}");
                }
                return null;
            }
        }

        /// <summary>
        /// Reads the pack from the command argument (a name or link) or, failing that, from the
        /// replied-to sticker. Replies with the reason and returns false if there is no valid pack.
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

            var sticker = update.Message.ReplyToMessage?.Sticker;
            if (sticker != null)
            {
                name = StickerSetName.Normalise(sticker.SetName);
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
