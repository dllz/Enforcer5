using System;
using System.Collections.Generic;
using System.Linq;
using Enforcer5.Handlers;
using Newtonsoft.Json;

namespace Enforcer5.LanguageUpload
{
    /// <summary>How much a problem matters for uploading the file.</summary>
    internal enum IssueSeverity
    {
        /// <summary>Never blocks: missing strings fall back to English.</summary>
        Info,
        /// <summary>Blocks until an admin accepts it (for every file that has it) or picks the file by hand.</summary>
        Warning,
        /// <summary>The file cannot be uploaded at all.</summary>
        Fatal
    }

    /// <summary>One file of an upload, as it was checked when the upload came in.</summary>
    internal class BatchFile
    {
        /// <summary>The file name with .xml: what it replaces live.</summary>
        public string FileName { get; set; }
        public string Name { get; set; }
        public string Base { get; set; }
        /// <summary>No live file has this language yet.</summary>
        public bool IsNewLanguage { get; set; }
        /// <summary>Strings missing from the live file it replaces (null for a new language).</summary>
        public int? LiveMissing { get; set; }
        public List<LanguageError> Errors { get; set; } = new List<LanguageError>();

        public static IssueSeverity SeverityOf(LanguageError e) =>
            LanguageValidator.IsFatal(e) ? IssueSeverity.Fatal
            : e.Level == ErrorLevel.MissingString ? IssueSeverity.Info
            : IssueSeverity.Warning;

        [JsonIgnore] public bool IsFatal => Errors.Any(e => SeverityOf(e) == IssueSeverity.Fatal);
        [JsonIgnore] public IEnumerable<LanguageError> Warnings => Errors.Where(e => SeverityOf(e) == IssueSeverity.Warning);
        [JsonIgnore] public IEnumerable<LanguageError> Fatals => Errors.Where(e => SeverityOf(e) == IssueSeverity.Fatal);
        [JsonIgnore] public int Missing => Errors.Count(e => e.Level == ErrorLevel.MissingString);
        [JsonIgnore] public bool IsClean => !IsFatal && !Warnings.Any();
    }

    /// <summary>
    /// The same warning (kind and string key) in every file of the upload that has it. Accepting it lets it through
    /// in all of them; the same kind of warning on another string is a separate group.
    /// </summary>
    internal class IssueGroup
    {
        public string Id { get; set; }
        public ErrorLevel Level { get; set; }
        public string Key { get; set; }
        /// <summary>The distinct messages (placeholder warnings say which placeholder).</summary>
        public List<string> Messages { get; set; }
        public List<string> Files { get; set; }

        public static string IdOf(LanguageError e) => $"{e.Level}|{e.Key}";
    }

    /// <summary>
    /// An upload of one or more language files waiting for an admin's decision, and what has been decided so far.
    /// Pure state: saved by <see cref="BatchStore"/>, shown by <see cref="BatchMenu"/>, published by
    /// <see cref="LanguagePublisher"/>.
    /// </summary>
    internal class LanguageBatch
    {
        public string Id { get; set; }
        public long ChatId { get; set; }
        /// <summary>The bot's message in the chat everyone sees: what came in, later what was uploaded.</summary>
        public int PublicMessageId { get; set; }
        public DateTime CreatedUtc { get; set; }
        public List<BatchFile> Files { get; set; } = new List<BatchFile>();
        /// <summary>Accepted <see cref="IssueGroup.Id"/>s.</summary>
        public HashSet<string> AcceptedIssues { get; set; } = new HashSet<string>();
        /// <summary>Files an admin picked or dropped by hand, overriding the accepted warnings.</summary>
        public Dictionary<string, bool> Picked { get; set; } = new Dictionary<string, bool>();
        /// <summary>Set when the batch was uploaded or discarded; nothing changes after that.</summary>
        public string Outcome { get; set; }

        [JsonIgnore] public bool IsOpen => Outcome == null;

        /// <summary>The warning groups, most widespread first. Callbacks refer to them by index, so the order is fixed.</summary>
        public List<IssueGroup> IssueGroups() =>
            Files.SelectMany(f => f.Warnings.Select(e => (File: f.FileName, Error: e)))
                .GroupBy(x => IssueGroup.IdOf(x.Error))
                .Select(g => new IssueGroup
                {
                    Id = g.Key,
                    Level = g.First().Error.Level,
                    Key = g.First().Error.Key,
                    Messages = g.Select(x => x.Error.Message).Where(m => !string.IsNullOrEmpty(m)).Distinct().ToList(),
                    Files = g.Select(x => x.File).Distinct().ToList()
                })
                .OrderByDescending(g => g.Files.Count).ThenBy(g => g.Level).ThenBy(g => g.Key, StringComparer.Ordinal)
                .ToList();

        public bool IsAccepted(IssueGroup group) => AcceptedIssues.Contains(group.Id);

        /// <summary>
        /// Whether the file goes up with "Upload selected": never when fatal; otherwise as picked by hand, or else
        /// once every warning it has is accepted.
        /// </summary>
        public bool IsSelected(BatchFile file)
        {
            if (file.IsFatal) return false;
            if (Picked.TryGetValue(file.FileName, out var picked)) return picked;
            return file.Warnings.All(e => AcceptedIssues.Contains(IssueGroup.IdOf(e)));
        }

        public List<BatchFile> Selected() => Files.Where(IsSelected).ToList();
        public List<BatchFile> Clean() => Files.Where(f => f.IsClean).ToList();

        /// <summary>Accepts or un-accepts a warning group. False if the index is not a group.</summary>
        public bool ToggleIssue(int index)
        {
            var groups = IssueGroups();
            if (index < 0 || index >= groups.Count) return false;
            var id = groups[index].Id;
            if (!AcceptedIssues.Remove(id)) AcceptedIssues.Add(id);
            return true;
        }

        /// <summary>Picks or drops a file by hand. False if it cannot be picked (no such file, or fatal).</summary>
        public bool ToggleFile(int index)
        {
            if (index < 0 || index >= Files.Count || Files[index].IsFatal) return false;
            var file = Files[index];
            Picked[file.FileName] = !IsSelected(file);
            return true;
        }
    }
}
