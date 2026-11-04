using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;
using System.Text;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Ids the bot shows (users, groups, channels, files, stickers) can be copied with a tap: they are sent as
    /// <c>&lt;code&gt;</c> (Daniel, 2026-11-04).
    /// <para>
    /// Where an id is put into a text it is marked with <see cref="Mark(long)"/>: two private-use characters
    /// around it, which survive the HTML escaping <see cref="Methods.GetLocaleString"/> gives its arguments (so a
    /// "Name (id)" built by a helper works inside any language string). <see cref="TelegramGateway"/>, which
    /// every request goes through, turns the marks into <c>&lt;code&gt;</c> in text sent as HTML and drops them
    /// everywhere else (plain text, button alerts, button labels, the plain resend after a parse failure).
    /// </para>
    /// <para>
    /// Names of people are mentions (Daniel, 2026-11-04): <see cref="Mention"/> marks a name with its user id the
    /// same way, and the gateway turns it into <c>&lt;a href="tg://user?id=…"&gt;name&lt;/a&gt;</c> in HTML and the
    /// bare name everywhere else. <see cref="Named"/> is the usual "Name (id)" with both.
    /// </para>
    /// Mark only what is shown: a marked id or name must never be stored, compared or used as a key.
    /// </summary>
    internal static class Ids
    {
        internal const char Open = '\uE000';
        internal const char Close = '\uE001';

        internal static string Mark(long id) => $"{Open}{id}{Close}";

        internal static string Mark(long? id) => id is { } v ? Mark(v) : "";

        internal static string Mark(string id) => string.IsNullOrEmpty(id) ? id : $"{Open}{id}{Close}";

        /// <summary>Starts a mention: the user id follows, then <see cref="MentionName"/>.</summary>
        internal const char MentionOpen = '\uE002';
        /// <summary>Ends a mention's user id; the name follows, then <see cref="MentionClose"/>.</summary>
        internal const char MentionName = '\uE003';
        internal const char MentionClose = '\uE004';

        private static readonly char[] AllMarks = { Open, Close, MentionOpen, MentionName, MentionClose };

        /// <summary>
        /// A person's name that links to them where the text is HTML. <paramref name="name"/> is the plain name, for
        /// a language argument or anything else that escapes it later; a name built straight into HTML uses
        /// <see cref="MentionHtml"/>. Without a name the id is shown.
        /// </summary>
        internal static string Mention(long userId, string name)
        {
            var shown = string.IsNullOrWhiteSpace(name) ? userId.ToString() : Clean(name);
            // Telegram's stand-ins (an anonymous admin, a post as a channel, Telegram itself) and chats are no one to
            // link to.
            if (userId <= 0 || Data.ObservationBuffer.Placeholders.Contains(userId)) return shown;
            return $"{MentionOpen}{userId}{MentionName}{shown}{MentionClose}";
        }

        /// <summary><see cref="Mention"/> with the name HTML-escaped, for text built straight into HTML.</summary>
        internal static string MentionHtml(long userId, string name) =>
            Mention(userId, string.IsNullOrWhiteSpace(name) ? userId.ToString() : Methods.FormatHTML(Clean(name)));

        /// <summary>"Name (id)": the name a mention, the id copyable. For language arguments (escaped later).</summary>
        internal static string Named(long userId, string name) => $"{Mention(userId, name)} ({Mark(userId)})";

        /// <summary><see cref="Named"/> for text built straight into HTML.</summary>
        internal static string NamedHtml(long userId, string name) => $"{MentionHtml(userId, name)} ({Mark(userId)})";

        // A name holding our own marks (pasted by someone) must not break the mention around it.
        private static string Clean(string name) => HasMarks(name) ? Strip(name) : name;

        internal static bool HasMarks(string text) => text != null && text.AsSpan().IndexOfAny(AllMarks) >= 0;

        /// <summary>The text without marks: ids plain, mentions just the name.</summary>
        internal static string Strip(string text)
        {
            if (!HasMarks(text)) return text;
            var sb = new StringBuilder(text.Length);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == MentionOpen)
                {
                    var name = text.IndexOf(MentionName, i + 1);
                    if (name >= 0 && IsId(text, i + 1, name)) i = name; // the id goes, the name stays
                    continue;
                }
                if (Array.IndexOf(AllMarks, c) < 0) sb.Append(c);
            }
            return sb.ToString();
        }

        private static bool IsId(string text, int from, int to)
        {
            if (to <= from || to - from > 20) return false;
            for (var k = from; k < to; k++)
                if (!(char.IsAsciiDigit(text[k]) || (k == from && text[k] == '-'))) return false;
            return true;
        }

        /// <summary>
        /// HTML with each marked id as <c>&lt;code&gt;</c>. Telegram cannot nest code in a link, another code or
        /// pre, or put a tag in a tag's attribute (an admin's <c>href="tg://user?id=$id"</c>): there the id is left
        /// plain. A mark without its other half (a long message split in two) is dropped. Telegram keeps at most
        /// <see cref="MaxEntities"/> formatted parts of a message, so ids past that stay plain too.
        /// A mention becomes a <c>tg://user</c> link under the same rules; where a link cannot go it is the bare name.
        /// </summary>
        internal static string ToHtml(string html)
        {
            if (!HasMarks(html)) return html;
            var sb = new StringBuilder(html.Length + 16);
            var noCode = 0; // open <a>, <code> and <pre> tags around this point
            var room = MaxEntities - OpeningTag.Matches(html).Count;
            for (var i = 0; i < html.Length; i++)
            {
                var c = html[i];
                if (c == '<')
                {
                    var end = html.IndexOf('>', i);
                    if (end < 0) { sb.Append(Strip(html.Substring(i))); break; }
                    var tag = html.Substring(i, end - i + 1);
                    noCode = Math.Max(0, noCode + NoCodeChange(tag));
                    sb.Append(Strip(tag));
                    i = end;
                    continue;
                }
                if (c == MentionOpen)
                {
                    // MentionOpen id MentionName name MentionClose; the name was escaped with the rest of the text
                    var nameAt = html.IndexOf(MentionName, i + 1);
                    if (nameAt < 0 || !IsId(html, i + 1, nameAt)) continue; // no id: drop this mark
                    var userId = html.Substring(i + 1, nameAt - i - 1);
                    var endAt = html.IndexOf(MentionClose, nameAt + 1);
                    var cut = html.IndexOfAny(new[] { '<', '>', MentionOpen, Open }, nameAt + 1);
                    if (endAt < 0 || (cut >= 0 && cut < endAt)) { i = nameAt; continue; } // no end: the name stays plain
                    var shown = html.Substring(nameAt + 1, endAt - nameAt - 1);
                    if (noCode == 0 && shown.Length > 0 && room-- > 0)
                        sb.Append("<a href=\"tg://user?id=").Append(userId).Append("\">").Append(shown).Append("</a>");
                    else sb.Append(shown);
                    i = endAt;
                    continue;
                }
                if (c == Close || c == MentionName || c == MentionClose) continue;
                if (c != Open) { sb.Append(c); continue; }
                var close = html.IndexOf(Close, i + 1);
                var next = html.IndexOfAny(new[] { Open, '<', '>', MentionOpen, MentionName, MentionClose }, i + 1);
                if (close < 0 || (next >= 0 && next < close)) continue; // no other half: drop this one
                var id = html.Substring(i + 1, close - i - 1);
                if (noCode == 0 && id.Length > 0 && room-- > 0) sb.Append("<code>").Append(id).Append("</code>");
                else sb.Append(id);
                i = close;
            }
            return sb.ToString();
        }

        /// <summary>Formatted parts Telegram keeps in one message.</summary>
        internal const int MaxEntities = 100;

        private static readonly System.Text.RegularExpressions.Regex OpeningTag = new("<[a-zA-Z]");

        private static int NoCodeChange(string tag)
        {
            var closing = tag.StartsWith("</", StringComparison.Ordinal);
            var name = new string(tag.Skip(closing ? 2 : 1).TakeWhile(char.IsLetter).ToArray()).ToLowerInvariant();
            if (name is not ("a" or "code" or "pre")) return 0;
            return closing ? -1 : 1;
        }

        // ---- Requests ----

        private static readonly Assembly TelegramTypes = typeof(ParseMode).Assembly;
        private static readonly ConcurrentDictionary<Type, Shape> Shapes = new();

        private sealed record Shape(PropertyInfo ParseMode, PropertyInfo[] Strings, PropertyInfo[] Children);

        /// <summary>
        /// Turns the marks in a request into what its parse mode shows: <c>&lt;code&gt;</c> in HTML text and
        /// captions, nothing anywhere else. Walks the request and the Telegram types in it (inline results, media,
        /// buttons).
        /// </summary>
        internal static void Apply(object request) => Apply(request, 0);

        private static void Apply(object node, int depth)
        {
            if (node == null || depth > 8) return;
            if (node is string) return;
            if (node is IEnumerable list)
            {
                foreach (var item in list) Apply(item, depth + 1);
                return;
            }
            var type = node.GetType();
            if (type.Assembly != TelegramTypes && depth > 0) return;
            var shape = Shapes.GetOrAdd(type, Build);
            var html = shape.ParseMode?.GetValue(node) is ParseMode.Html;
            foreach (var p in shape.Strings)
            {
                if (p.GetValue(node) is not string s || !HasMarks(s)) continue;
                p.SetValue(node, html && IsFormatted(p.Name) ? ToHtml(s) : Strip(s));
            }
            foreach (var p in shape.Children) Apply(p.GetValue(node), depth + 1);
        }

        // The texts a parse mode applies to; titles, descriptions, alerts and button labels are always plain.
        private static bool IsFormatted(string property) => property is "Text" or "Caption" or "MessageText";

        private static Shape Build(Type type)
        {
            var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead).ToArray();
            var parseMode = props.FirstOrDefault(p => p.Name == "ParseMode" &&
                (p.PropertyType == typeof(ParseMode) || p.PropertyType == typeof(ParseMode?)));
            var strings = props.Where(p => p.PropertyType == typeof(string) && p.CanWrite).ToArray();
            var children = props.Where(p => p.PropertyType != typeof(string) && !p.PropertyType.IsValueType &&
                (p.PropertyType.Assembly == TelegramTypes || typeof(IEnumerable).IsAssignableFrom(p.PropertyType)) &&
                !typeof(System.IO.Stream).IsAssignableFrom(p.PropertyType)).ToArray();
            return new Shape(parseMode, strings, children);
        }
    }
}
