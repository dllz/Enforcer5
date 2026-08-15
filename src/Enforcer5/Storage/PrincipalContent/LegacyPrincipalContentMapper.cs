using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Enforcer5.Storage.PrincipalContent
{
    internal static class LegacyPrincipalContentMapper
    {
        private const string FilePrefix = "###file_id!";
        private const string FileSeparator = "###:";

        internal static IReadOnlyList<GroupContentItem> MapContent(
            long chatId, string rules, string about, string language, string link)
        {
            var result = new List<GroupContentItem>(4);
            AddIfPresent(result, chatId, GroupContentTypes.Rules, rules);
            AddIfPresent(result, chatId, GroupContentTypes.About, about);
            AddIfPresent(result, chatId, GroupContentTypes.Language, language);
            AddIfPresent(result, chatId, GroupContentTypes.Link, link);
            return result;
        }

        internal static CustomCommand ParseCustomCommand(
            long chatId, string trigger, string rawValue, string mediaId, string caption)
        {
            if (rawValue == null) throw new ArgumentNullException(nameof(rawValue));
            trigger = NormalizeCustomCommandTrigger(trigger);

            if (rawValue.StartsWith(FilePrefix, StringComparison.Ordinal))
            {
                var separator = rawValue.IndexOf(FileSeparator, FilePrefix.Length, StringComparison.Ordinal);
                if (separator > FilePrefix.Length)
                {
                    var type = NormalizeCustomCommandMediaType(
                        rawValue.Substring(FilePrefix.Length, separator - FilePrefix.Length));
                    var fileId = rawValue.Substring(separator + FileSeparator.Length);
                    if (fileId.Length > 0)
                        return new CustomCommand(chatId, trigger, null, type, fileId, caption, "legacy_unknown");
                }
            }

            // GIF commands store their Telegram file id in a side hash and retain text in the
            // main hash. Other media encodes type and file id directly in the main value.
            return string.IsNullOrEmpty(mediaId)
                ? new CustomCommand(chatId, trigger, rawValue, null, null, null)
                : new CustomCommand(chatId, trigger, rawValue, "animation", mediaId, null, "legacy_unknown");
        }

        internal static string NormalizeCustomCommandMediaType(string mediaType) => mediaType switch
        {
            "gif" => "animation",
            "file" or "text" or "unknown" => "document",
            "videoNote" => "video_note",
            _ => mediaType
        };

        internal static string NormalizeCustomCommandTrigger(string trigger)
        {
            if (trigger == null) throw new ArgumentNullException(nameof(trigger));

            var value = trigger.TrimStart('#').Normalize(NormalizationForm.FormC);
            var length = 0;
            while (length < value.Length)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(value, length);
                if (category != UnicodeCategory.UppercaseLetter &&
                    category != UnicodeCategory.LowercaseLetter &&
                    category != UnicodeCategory.TitlecaseLetter &&
                    category != UnicodeCategory.ModifierLetter &&
                    category != UnicodeCategory.OtherLetter &&
                    category != UnicodeCategory.DecimalDigitNumber &&
                    category != UnicodeCategory.NonSpacingMark &&
                    category != UnicodeCategory.ConnectorPunctuation)
                    break;
                length += char.IsSurrogatePair(value, length) ? 2 : 1;
            }

            return (length == 0 ? value : value.Substring(0, length)).ToLowerInvariant();
        }

        private static void AddIfPresent(List<GroupContentItem> result, long chatId,
            string contentType, string value)
        {
            if (value != null) result.Add(new GroupContentItem(chatId, contentType, value));
        }
    }
}
