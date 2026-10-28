using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Enforcer5.Detection
{
    /// <summary>
    /// Whether the reason an admin gave for a decision (<c>/ban spam</c>, <c>/warn ads</c>) names spam, ads,
    /// scams, porn or raids, in the languages Enforcer has files for (build step 7c, Daniel 2026-10-28). The
    /// reason is already in the history (<c>sanctions.reason</c>); this only reads it, and a label keeps no
    /// more than abuse or not. Whole words for scripts with spaces, parts of words for Chinese and Persian.
    /// A word for a group's own rules (rules, off-topic, a game's terms) is deliberately not here.
    /// </summary>
    internal static class AbuseWords
    {
        // Folded like the reason before comparing.
        private static readonly string[] Words =
        {
            // English
            "spam", "spammer", "spamming", "spambot", "ad", "ads", "advert", "adverts", "advertising", "advertisement",
            "promo", "promotion", "promoting", "scam", "scammer", "scamming", "fraud", "phishing", "porn", "porno", "nsfw",
            "raid", "raider", "raiding", "impersonation", "impersonating", "impostor", "fake admin",
            // German, Italian, Spanish, Portuguese
            "werbung", "betrug", "betruger", "pubblicita", "truffa", "truffatore", "publicidad", "anuncio", "estafa", "estafador",
            "propaganda", "anuncio", "golpe", "golpista", "fraude",
            // Turkish, Indonesian
            "reklam", "dolandirici", "dolandiricilik", "iklan", "promosi", "penipuan", "penipu",
            // Russian
            "спам", "спамер", "реклама", "рекламу", "мошенник", "мошенничество", "скам", "порно", "рейд"
        };

        private static readonly string[] Parts =
        {
            // Chinese (both scripts) and Persian
            "广告", "廣告", "诈骗", "詐騙", "骗子", "騙子", "色情", "垃圾信息", "垃圾訊息", "刷屏",
            "تبلیغ", "اسپم", "کلاهبردار", "پورن"
        };

        private static readonly string[] Folded = Words.Select(Fold).Distinct().ToArray();

        internal static bool Mentions(string reason)
        {
            if (string.IsNullOrWhiteSpace(reason)) return false;
            var text = Fold(reason);
            if (Parts.Any(text.Contains)) return true;
            var words = text.Split(new[] { ' ', ',', '.', ';', ':', '!', '?', '/', '-', '_', '(', ')', '"', '\'', '\n', '\t', '#' },
                StringSplitOptions.RemoveEmptyEntries);
            foreach (var word in Folded)
            {
                if (word.Contains(' '))
                {
                    if ((" " + string.Join(" ", words) + " ").Contains(" " + word + " ")) return true;
                }
                else if (words.Contains(word)) return true;
            }
            return false;
        }

        private static string Fold(string text)
        {
            var decomposed = text.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var folded = new StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
                if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) folded.Append(c);
            // ı (dotless i) folds to i, so Turkish "dolandırıcı" matches.
            return folded.ToString().Normalize(NormalizationForm.FormC).Replace('ı', 'i');
        }
    }
}
