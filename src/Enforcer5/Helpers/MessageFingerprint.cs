using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// What a message is like, without what it says: enough to see that raiders all post the same
    /// text or image, write in the same script, or post nothing but a link. The text itself is never
    /// kept; only a hash of it with case, spacing, punctuation, digits, accents and lookalike letters
    /// taken out, so "Join NOW!!", "join now 2" and "Jоin now" (a Cyrillic о) match.
    /// </summary>
    internal sealed record MessageFingerprint(string ContentType, string TextHash, string MediaUniqueId, string Script,
        int LinkCount, bool BareLink)
    {
        private static readonly Regex LinkPattern = new Regex(@"(https?://\S+|t\.me/\S+|telegram\.me/\S+|www\.\S+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal static MessageFingerprint Of(Message message) => Of(message, null);

        /// <summary>
        /// As <see cref="Of(Message)"/>, with what is known of where each @mention points
        /// (<paramref name="mentionOutside"/>: true outside the group, false inside it, null not known).
        /// A mention inside the group is a word like any other; one outside it is a link; one not known
        /// is a link, but a bare link only when there is nothing else in the message.
        /// </summary>
        internal static MessageFingerprint Of(Message message, Func<MessageEntity, bool?> mentionOutside)
        {
            var text = message.Text ?? message.Caption ?? "";
            var entities = message.Entities ?? message.CaptionEntities ?? Array.Empty<MessageEntity>();
            var (links, bare) = LinksIn(text, entities, mentionOutside);
            return new MessageFingerprint(
                ContentTypeOf(message),
                Hash(text),
                MediaIdOf(message),
                ScriptOf(text),
                links,
                bare);
        }

        /// <summary>"@admin" and the like: how members report, not a mention of anyone.</summary>
        internal static bool IsReportMention(string text, MessageEntity e)
        {
            if (e.Type != MessageEntityType.Mention || e.Offset < 0 || e.Offset + e.Length > text.Length) return false;
            var mention = text.Substring(e.Offset, e.Length);
            return mention.Equals("@admin", StringComparison.OrdinalIgnoreCase) ||
                   mention.Equals("@admins", StringComparison.OrdinalIgnoreCase) ||
                   mention.Equals("@pingall", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMention(MessageEntity e) => e.Type is MessageEntityType.Mention or MessageEntityType.TextMention;

        /// <summary>The kind of message, as the flood exceptions name them.</summary>
        internal static string ContentTypeOf(Message message)
        {
            if (message.Photo != null) return "photo";
            if (message.Animation != null) return "gif";
            if (message.Video != null) return "video";
            if (message.Sticker != null) return "sticker";
            if (message.Voice != null) return "voice";
            if (message.Audio != null) return "audio";
            if (message.Document != null) return "document";
            if (message.VideoNote != null) return "videonote";
            if (message.Contact != null) return "contact";
            if (message.Location != null) return "location";
            if (message.Text != null) return "text";
            return "other";
        }

        internal static string MediaIdOf(Message message) =>
            message.Photo?.OrderByDescending(p => p.Width * p.Height).FirstOrDefault()?.FileUniqueId
            ?? message.Animation?.FileUniqueId
            ?? message.Video?.FileUniqueId
            ?? message.Sticker?.FileUniqueId
            ?? message.Document?.FileUniqueId
            ?? message.Voice?.FileUniqueId
            ?? message.Audio?.FileUniqueId
            ?? message.VideoNote?.FileUniqueId;

        /// <summary>
        /// The letters of the text, lower case, with accents and other marks taken off and letters
        /// that only look Latin (Cyrillic "а", Greek "ο") made Latin: what spam is compared by, so
        /// lookalike letters and decorations do not make it different. Empty when there are none.
        /// </summary>
        internal static string Normalize(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var normal = new StringBuilder(text.Length);
            foreach (var c in text.Normalize(NormalizationForm.FormKD))
            {
                if (!char.IsLetter(c)) continue; // marks, digits, spaces, punctuation, invisible characters
                var lower = char.ToLowerInvariant(c);
                normal.Append(Lookalikes.TryGetValue(lower, out var latin) ? latin : lower);
            }
            return normal.ToString();
        }

        // Letters from other scripts that pass for Latin ones.
        private static readonly System.Collections.Generic.Dictionary<char, char> Lookalikes = new()
        {
            ['а'] = 'a', ['в'] = 'b', ['е'] = 'e', ['ё'] = 'e', ['к'] = 'k', ['м'] = 'm', ['н'] = 'h', ['о'] = 'o',
            ['р'] = 'p', ['с'] = 'c', ['т'] = 't', ['у'] = 'y', ['х'] = 'x', ['ѕ'] = 's', ['і'] = 'i', ['ј'] = 'j',
            ['ԁ'] = 'd', ['һ'] = 'h', ['ӏ'] = 'l', ['ԛ'] = 'q', ['ԝ'] = 'w',
            ['α'] = 'a', ['β'] = 'b', ['ε'] = 'e', ['η'] = 'n', ['ι'] = 'i', ['κ'] = 'k', ['ν'] = 'v', ['ο'] = 'o',
            ['ρ'] = 'p', ['τ'] = 't', ['υ'] = 'u', ['χ'] = 'x', ['ı'] = 'i'
        };

        /// <summary>A short hash of <see cref="Normalize"/>d text, so "Join NOW!!" and "join now 2" match; null when no letters are left.</summary>
        internal static string Hash(string text)
        {
            var normal = Normalize(text);
            if (normal.Length == 0) return null;
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normal));
            return Convert.ToHexString(hash, 0, 12).ToLowerInvariant();
        }

        /// <summary>
        /// The three-letter runs of the <see cref="Normalize"/>d text, hashed: two texts sharing most
        /// of them are the same text with a word or a random ending changed. Kept in memory for a
        /// minute only; null when there are no letters.
        /// </summary>
        internal static System.Collections.Generic.HashSet<int> Sketch(string text)
        {
            var normal = Normalize(text);
            if (normal.Length < 3) return null;
            var runs = new System.Collections.Generic.HashSet<int>();
            for (var i = 0; i + 3 <= normal.Length; i++)
                runs.Add(string.GetHashCode(normal.AsSpan(i, 3), StringComparison.Ordinal));
            return runs;
        }

        /// <summary>
        /// True when the message is nothing but links, give or take two words; or nothing but a
        /// mention, as channel ads are ("@someone thanks" is a reply, not a link post).
        /// </summary>
        internal static bool IsBareLink(string text, MessageEntity[] entities) => LinksIn(text, entities, null).Bare;

        /// <summary>
        /// How many links the text has, and whether it is nothing but links give or take two words.
        /// Mentions count by <paramref name="mentionOutside"/>, as in <see cref="Of(Message, Func{MessageEntity, bool?})"/>.
        /// </summary>
        internal static (int Links, bool Bare) LinksIn(string text, MessageEntity[] entities, Func<MessageEntity, bool?> mentionOutside)
        {
            if (string.IsNullOrWhiteSpace(text)) return (0, false);
            bool? Place(MessageEntity e) => mentionOutside?.Invoke(e);
            var mentions = entities.Where(e => IsMention(e) && !IsReportMention(text, e)).ToList();
            var outside = mentions.Where(e => Place(e) == true).ToList();
            var unknown = mentions.Where(e => Place(e) == null && e.Type == MessageEntityType.Mention).ToList();
            var urlEntities = entities.Count(e => e.Type is MessageEntityType.Url or MessageEntityType.TextLink);
            var urls = Math.Max(urlEntities, LinkPattern.Matches(text).Count);
            var links = urls + outside.Count + unknown.Count;
            if (links == 0) return (0, false);

            // Blank out the links and the mentions pointing outside (or not known) and see what is
            // left: a word that links (a text link), or a member mentioned, is still a word.
            var chars = text.ToCharArray();
            foreach (var e in entities.Where(e => e.Type == MessageEntityType.Url).Concat(outside).Concat(unknown))
                for (var i = e.Offset; i < e.Offset + e.Length && i < chars.Length; i++)
                    chars[i] = ' ';
            var rest = LinkPattern.Replace(new string(chars), " ");
            var words = rest.Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Count(w => w.Any(char.IsLetter));
            // "join <link>" or "free <link> now" is still a bare link: at most two words around it.
            if (urls > 0 || outside.Count > 0) return (links, words <= 2);
            // Only mentions nobody looked up: a bare link when there is nothing else at all.
            return (links, words == 0);
        }

        /// <summary>The script most of the letters are in: latin, cyrillic, arabic, cjk, hangul, devanagari, thai, hebrew, greek, other; null without letters.</summary>
        internal static string ScriptOf(string text)
        {
            if (string.IsNullOrEmpty(text)) return null;
            var counts = new System.Collections.Generic.Dictionary<string, int>();
            foreach (var c in text)
            {
                if (!char.IsLetter(c)) continue;
                var s = ScriptOfLetter(c);
                counts[s] = counts.TryGetValue(s, out var n) ? n + 1 : 1;
            }
            return counts.Count == 0 ? null : counts.OrderByDescending(p => p.Value).First().Key;
        }

        private static string ScriptOfLetter(char c) => c switch
        {
            <= '\u024F' => "latin",
            >= '\u0370' and <= '\u03FF' => "greek",
            >= '\u0400' and <= '\u052F' => "cyrillic",
            >= '\u0590' and <= '\u05FF' => "hebrew",
            >= '\u0600' and <= '\u06FF' or >= '\u0750' and <= '\u077F' or >= '\uFB50' and <= '\uFDFF' or >= '\uFE70' and <= '\uFEFF' => "arabic",
            >= '\u0900' and <= '\u097F' => "devanagari",
            >= '\u0E00' and <= '\u0E7F' => "thai",
            >= '\u1100' and <= '\u11FF' or >= '\uAC00' and <= '\uD7AF' or >= '\u3130' and <= '\u318F' => "hangul",
            >= '\u3040' and <= '\u30FF' or >= '\u4E00' and <= '\u9FFF' or >= '\u3400' and <= '\u4DBF' => "cjk",
            >= '\u1E00' and <= '\u1EFF' => "latin",
            _ => "other"
        };
    }
}
