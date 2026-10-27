using System.Collections.Generic;
using System.Linq;
using System.Text;
using Enforcer5.Handlers;

namespace Enforcer5.LanguageUpload
{
    /// <summary>The full plain-text report of an upload, every problem of every file; sent as a file on request.</summary>
    internal static class BatchReport
    {
        internal static string Render(LanguageBatch b)
        {
            var sb = new StringBuilder($"Language upload {b.Id} · {IssueText.Files(b.Files.Count)} · {b.CreatedUtc:yyyy-MM-dd HH:mm} UTC\n");
            foreach (var f in b.Files)
            {
                sb.Append("\n").Append(new string('=', 40)).Append('\n').Append(f.FileName);
                var language = IssueText.Language(f);
                if (language != null) sb.Append(" · ").Append(language);
                sb.Append('\n');
                sb.Append("Status: ").Append(f.IsFatal ? "cannot be uploaded" : f.IsClean ? "clean" : "warnings")
                  .Append(b.IsSelected(f) ? ", selected" : "").Append('\n');
                if (!f.IsFatal)
                    sb.Append(f.IsNewLanguage ? "New language: no live file has this language yet\n" : $"Live file: {f.LiveMissing} strings missing\n");

                Section(sb, "Cannot be uploaded", f.Fatals.Select(e => $"{e.Key}: {e.Message}"));
                Section(sb, "Placeholders", f.Warnings.Where(e => e.Level == ErrorLevel.Error).Select(e => $"{e.Key}: {e.Message}"));
                Section(sb, "Twice (the bot may show either copy)", f.Warnings.Where(e => e.Level == ErrorLevel.DuplicatedString).Select(e => e.Key));
                Section(sb, "GIF text over 200 characters", f.Warnings.Where(e => e.Level == ErrorLevel.GifTooLong).Select(e => e.Key));
                Section(sb, $"Missing strings ({f.Missing})", f.Errors.Where(e => e.Level == ErrorLevel.MissingString).Select(e => e.Key));
            }
            return sb.ToString();
        }

        private static void Section(StringBuilder sb, string title, IEnumerable<string> lines)
        {
            var list = lines.ToList();
            if (list.Count == 0) return;
            sb.Append(title).Append(":\n");
            foreach (var line in list) sb.Append("  ").Append(line).Append('\n');
        }
    }
}
