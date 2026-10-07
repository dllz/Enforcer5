using System;
using System.Text.RegularExpressions;

namespace Enforcer5.Models
{
    /// <summary>
    /// Sticker pack names as admins type them and as Telegram reports them in
    /// <c>Message.Sticker.SetName</c>.
    ///
    /// A pack is identified by its short name, the part after <c>t.me/addstickers/</c>. Admins can
    /// give that name or paste the link. Names are compared case-insensitively, like the links
    /// Telegram resolves them from, so one canonical lowercase form is stored and looked up.
    /// </summary>
    internal static class StickerSetName
    {
        /// <summary>Telegram's limit on a sticker set name.</summary>
        internal const int MaxLength = 64;

        private static readonly Regex NameFormat = new Regex(
            "^[A-Za-z0-9_]{1," + MaxLength + "}$", RegexOptions.CultureInvariant);

        // t.me/addstickers/NAME and telegram.me/addstickers/NAME, with or without a scheme, and the
        // tg://addstickers?set=NAME form. addemoji links name custom emoji packs, which are not
        // supported, but are accepted so the admin gets the specific explanation for that.
        private static readonly Regex LinkFormat = new Regex(
            @"^(?:(?:https?://)?(?:www\.)?(?:t|telegram)\.me/(?:addstickers|addemoji)/|tg://(?:addstickers|addemoji)\?set=)([^/?#\s]+)/?$",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        /// <summary>
        /// Reads a pack name or link. Returns the canonical (lowercase) name, or null if the input
        /// is neither.
        /// </summary>
        public static string Parse(string input)
        {
            var text = input?.Trim();
            if (string.IsNullOrEmpty(text)) return null;

            var link = LinkFormat.Match(text);
            if (link.Success) text = link.Groups[1].Value;

            return Normalise(text);
        }

        /// <summary>
        /// The canonical form of a name Telegram reported, or null if there is none (a sticker
        /// that belongs to no pack) or it does not look like a pack name.
        /// </summary>
        public static string Normalise(string name) =>
            name != null && NameFormat.IsMatch(name) ? name.ToLowerInvariant() : null;

        /// <summary>The link that opens the pack in Telegram.</summary>
        public static string Link(string name, bool customEmoji = false) =>
            $"https://t.me/{(customEmoji ? "addemoji" : "addstickers")}/{name}";
    }
}
