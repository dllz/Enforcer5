using System.Collections.Generic;
using System.Linq;
using Enforcer5.Handlers;

namespace Enforcer5.LanguageUpload
{
    /// <summary>How problems are named for admins, in the menu and in the report alike.</summary>
    internal static class IssueText
    {
        /// <summary>A warning group in a few words, e.g. "warnUser: Missing {1}".</summary>
        internal static string Label(IssueGroup g) => g.Level switch
        {
            ErrorLevel.DuplicatedString => $"{g.Key} twice",
            ErrorLevel.GifTooLong => $"{g.Key} too long for a GIF",
            _ => g.Messages.Count > 0 ? $"{g.Key}: {string.Join(", ", g.Messages)}" : g.Key
        };

        /// <summary>What a kind of warning does in the bot, so it can be judged.</summary>
        internal static string Meaning(ErrorLevel level) => level switch
        {
            ErrorLevel.DuplicatedString => "twice: the bot may show either copy of the string",
            ErrorLevel.GifTooLong => "too long for a GIF: the GIF's text is over 200 characters and may not be sent",
            ErrorLevel.Error => "placeholder: a missing {0} loses a name or number; an extra one makes the bot show English instead",
            _ => null
        };

        /// <summary>Why a file cannot be uploaded, in one line.</summary>
        internal static string FatalReason(BatchFile f)
        {
            var e = f.Fatals.FirstOrDefault();
            if (e == null) return null;
            var reason = e.Key == BatchBuilder.FileKey || e.Key == LanguageValidator.LanguageNodeKey ? e.Message
                : $"{e.Key}: {e.Message}";
            var more = f.Fatals.Count() - 1;
            return more > 0 ? $"{reason} (+{more} more)" : reason;
        }

        /// <summary>"name (base)" of a file that could be read.</summary>
        internal static string Language(BatchFile f) => f.Name == null ? null : $"{f.Name} ({f.Base})";

        internal static string Files(int n) => n == 1 ? "1 file" : $"{n} files";

        /// <summary>The names, or "N files" when there are too many to list.</summary>
        internal static string FileList(IList<string> names, int max = 3) =>
            names.Count <= max ? string.Join(", ", names) : Files(names.Count);
    }
}
