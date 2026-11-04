using System.Text.RegularExpressions;
using Enforcer5.Helpers;
using Telegram.Bot.Types;

namespace Enforcer5
{
    /// <summary>
    /// The text of a group's custom welcome (/welcome &lt;text&gt;) for one new member. Pure, so it can be tested
    /// without Telegram.
    /// </summary>
    internal static class WelcomeText
    {
        // A whole link (its text may hold $name), any other tag (an attribute may), or $name on its own.
        private static readonly Regex LinkTagOrName = new(@"<a\b[^>]*>.*?</a\s*>|<[^>]*>|(?-i:\$name)",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);

        /// <summary>The member's name as a link that opens their profile.</summary>
        internal static string Mention(User user) => Detection.DetectionReports.Mention(user.Id, user.FirstName);

        /// <summary>
        /// The welcome to send as HTML. <c>$name</c> becomes a link to the member's profile (Daniel, 2026-11-03),
        /// except where the admin wrote a link around it themselves (<c>&lt;a href="tg://user?id=$id"&gt;$name&lt;/a&gt;</c>)
        /// or put it inside a tag: there it is the plain name, since a link cannot hold another.
        /// </summary>
        internal static string Html(string template, User joiner, string chatTitle)
        {
            var name = joiner.FirstName.FormatHTML();
            var text = LinkTagOrName.Replace(template ?? "", m =>
                m.Value == "$name"
                    ? Mention(joiner)
                    : m.Value.Replace("$name", name));
            // $id can be copied with a tap, except inside a tag or the admin's own link or code (Ids.ToHtml).
            return Fill(text, joiner, chatTitle.FormatHTML(), Ids.Mark(joiner.Id));
        }

        /// <summary>
        /// The same welcome unformatted, for when Telegram cannot parse the admin's HTML: as written, with the
        /// plain name, as it was sent before the link.
        /// </summary>
        internal static string Plain(string template, User joiner, string chatTitle) =>
            Fill((template ?? "").Replace("$name", joiner.FirstName), joiner, chatTitle, joiner.Id.ToString());

        private static readonly Regex FormattingTag = new(@"</?(b|strong|i|em|u|ins|s|strike|del|code|pre|a|tg-spoiler|blockquote)(\s[^>]*)?>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        /// <summary>
        /// HTML the bot built (the default welcome with the rules, about or admin list) without its markup, for
        /// when Telegram cannot parse it: the formatting tags dropped and the escapes undone.
        /// </summary>
        internal static string Unformat(string html) =>
            System.Net.WebUtility.HtmlDecode(FormattingTag.Replace(html ?? "", ""));

        private static string Fill(string text, User joiner, string chatTitle, string id)
        {
            var username = string.IsNullOrEmpty(joiner.Username) ? "(no username)" : $"@{joiner.Username}";
            return text.Replace("$username", username)
                .Replace("$id", id)
                .Replace("$title", chatTitle);
        }
    }
}
