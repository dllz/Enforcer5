using System;
using System.Collections.Generic;

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

            if (rawValue.StartsWith(FilePrefix, StringComparison.Ordinal))
            {
                var separator = rawValue.IndexOf(FileSeparator, FilePrefix.Length, StringComparison.Ordinal);
                if (separator > FilePrefix.Length)
                {
                    var type = rawValue.Substring(FilePrefix.Length, separator - FilePrefix.Length);
                    var fileId = rawValue.Substring(separator + FileSeparator.Length);
                    if (fileId.Length > 0)
                        return new CustomCommand(chatId, trigger, null, type, fileId, caption);
                }
            }

            // GIF commands store their Telegram file id in a side hash and retain text in the
            // main hash. Other media encodes type and file id directly in the main value.
            return string.IsNullOrEmpty(mediaId)
                ? new CustomCommand(chatId, trigger, rawValue, null, null, null)
                : new CustomCommand(chatId, trigger, rawValue, "gif", mediaId, null);
        }

        private static void AddIfPresent(List<GroupContentItem> result, long chatId,
            string contentType, string value)
        {
            if (value != null) result.Add(new GroupContentItem(chatId, contentType, value));
        }
    }
}
