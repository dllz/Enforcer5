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
    /// Mark only what is shown: a marked id must never be stored, compared or used as a key.
    /// </summary>
    internal static class Ids
    {
        internal const char Open = '\uE000';
        internal const char Close = '\uE001';

        internal static string Mark(long id) => $"{Open}{id}{Close}";

        internal static string Mark(long? id) => id is { } v ? Mark(v) : "";

        internal static string Mark(string id) => string.IsNullOrEmpty(id) ? id : $"{Open}{id}{Close}";

        internal static bool HasMarks(string text) => text != null && text.AsSpan().IndexOfAny(Open, Close) >= 0;

        /// <summary>The text without marks.</summary>
        internal static string Strip(string text) =>
            HasMarks(text) ? text.Replace(Open.ToString(), "").Replace(Close.ToString(), "") : text;

        /// <summary>
        /// HTML with each marked id as <c>&lt;code&gt;</c>. Telegram cannot nest code in a link, another code or
        /// pre, or put a tag in a tag's attribute (an admin's <c>href="tg://user?id=$id"</c>): there the id is left
        /// plain. A mark without its other half (a long message split in two) is dropped. Telegram keeps at most
        /// <see cref="MaxEntities"/> formatted parts of a message, so ids past that stay plain too.
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
                if (c == Close) continue;
                if (c != Open) { sb.Append(c); continue; }
                var close = html.IndexOf(Close, i + 1);
                var next = html.IndexOfAny(new[] { Open, '<', '>' }, i + 1);
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
