using System;
using System.Linq;
using System.Text;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Detection
{
    /// <summary>
    /// The text of a message a sign fired on, as the admins are shown it (Daniel, 2026-11-06: "I can't ever get a message
    /// saying someone did x without being able to see it"). Kept in <c>detections.message_text</c> for as long as a report
    /// can show it (<see cref="Data.IDetectionDataRepository.ApplyRetentionAsync"/>), and shown by the report's Show messages.
    /// What a reader of the message cannot see is written out: a hidden link's address after its words, the id of
    /// someone mentioned without a username.
    /// </summary>
    internal static class FlaggedText
    {
        /// <summary>As long as a Telegram message can be.</summary>
        internal const int MaxLength = 4096;

        /// <summary>The message's text or caption with hidden links and mentions written out; null when it has none. Pure, for tests.</summary>
        internal static string Of(Message message)
        {
            if (message == null) return null;
            var text = message.Text ?? message.Caption;
            if (string.IsNullOrWhiteSpace(text)) return null;
            var entities = (message.Text != null ? message.Entities : message.CaptionEntities) ?? Array.Empty<MessageEntity>();
            var hidden = entities
                .Where(e => e.Offset >= 0 && e.Length > 0 && e.Offset + e.Length <= text.Length &&
                            (e.Type == MessageEntityType.TextLink && !string.IsNullOrEmpty(e.Url) ||
                             e.Type == MessageEntityType.TextMention && e.User != null))
                .OrderBy(e => e.Offset + e.Length)
                .ToList();
            var result = new StringBuilder(text.Length + hidden.Count * 24);
            var at = 0;
            foreach (var e in hidden)
            {
                var end = e.Offset + e.Length;
                if (end < at) continue; // entities never overlap; should one, it is written out once
                result.Append(text, at, end - at);
                result.Append(e.Type == MessageEntityType.TextLink ? $" [{e.Url}]" : $" [id {e.User.Id}]");
                at = end;
            }
            result.Append(text, at, text.Length - at);
            return Cut(result.ToString());
        }

        /// <summary><paramref name="text"/> cut at <see cref="MaxLength"/>.</summary>
        internal static string Cut(string text) =>
            text == null || text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…";
    }
}
