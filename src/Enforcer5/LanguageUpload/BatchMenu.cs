using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Enforcer5.Helpers;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5.LanguageUpload
{
    /// <summary>What a decision button does.</summary>
    internal enum BatchAction
    {
        UploadClean = 0,
        UploadSelected = 1,
        Discard = 2
    }

    /// <summary>One screen of the menu: HTML text and its buttons.</summary>
    internal class Screen
    {
        public string Text { get; set; }
        public InlineKeyboardMarkup Markup { get; set; }
    }

    /// <summary>
    /// Renders a <see cref="LanguageBatch"/>: the one message everyone in the chat sees, and the review menu only the
    /// admin who opened it sees. Pure: no Telegram calls, no files.
    /// Callback data is <c>lb:{batch id}:{op}:{number}</c>, at most about 25 bytes whatever the file names.
    /// </summary>
    internal static class BatchMenu
    {
        internal const string Prefix = "lb";
        internal const int PageSize = 8;
        private const int MaxText = 3900; // Telegram allows 4096 after entities are parsed

        internal static class Ops
        {
            internal const string Open = "open", Summary = "sum", Issues = "iss", ToggleIssue = "ti", Files = "fil",
                ToggleFile = "tf", Ask = "ask", Do = "go", Report = "rep", Close = "x";
        }

        internal static string Data(string id, string op, int arg = 0) => $"{Prefix}:{id}:{op}:{arg}";

        /// <summary>Reads callback data made by <see cref="Data"/>, split on ':'; false for anything else.</summary>
        internal static bool TryParse(string[] args, out string id, out string op, out int arg)
        {
            id = op = null;
            arg = 0;
            if (args.Length != 4 || args[0] != Prefix || !BatchStore.IsValidId(args[1]) || !int.TryParse(args[3], out arg))
                return false;
            id = args[1];
            op = args[2];
            return true;
        }

        // ---- the message everyone sees ----

        internal static Screen Public(LanguageBatch b) => new Screen
        {
            Text = Header(b) + "\n" + Counts(b) + "\nA language admin taps Review to choose what goes live.",
            Markup = Keyboard(new[] { Button("🔍 Review", b, Ops.Open) })
        };

        /// <summary>The public message once decided: what went live and what did not, and who decided.</summary>
        internal static string Result(LanguageBatch b, IList<PublishOutcome> outcomes, string by)
        {
            var published = outcomes.Where(o => o.Published).Select(o => o.FileName).ToList();
            var skipped = outcomes.Where(o => !o.Published && (o.Reason == "not selected" || o.Reason == "cannot be uploaded")).ToList();
            var failed = outcomes.Where(o => !o.Published).Except(skipped).ToList();
            var sb = new StringBuilder(Header(b)).Append('\n')
                .Append($"<b>Uploaded {published.Count} of {b.Files.Count}</b> · by {by.FormatHTML()}\n");
            if (published.Count > 0)
                sb.Append("✅ ").Append(string.Join(", ", published).FormatHTML()).Append('\n');
            if (skipped.Count > 0)
                sb.Append("⏭ ").Append(string.Join(", ", skipped.Select(o => $"{o.FileName} ({o.Reason})")).FormatHTML()).Append('\n');
            if (failed.Count > 0)
                sb.Append("❌ ").Append(string.Join(", ", failed.Select(o => $"{o.FileName} ({o.Reason})")).FormatHTML()).Append('\n');
            return Fit(sb.ToString());
        }

        internal static string Discarded(LanguageBatch b, string by) =>
            Header(b) + $"\n<b>Discarded</b> · by {by.FormatHTML()}: nothing uploaded.";

        // ---- the review menu ----

        internal static Screen Summary(LanguageBatch b)
        {
            var groups = b.IssueGroups();
            var sb = new StringBuilder(Header(b)).Append('\n').Append(Counts(b)).Append('\n');
            var missing = b.Files.Count(f => f.Missing > 0);
            if (missing > 0)
                sb.Append($"ℹ️ Strings missing in {IssueText.Files(missing)} (English is shown; never blocks)\n");

            if (groups.Count > 0)
            {
                sb.Append("\n<b>Warnings</b>\n");
                foreach (var g in groups)
                    sb.Append(Check(b.IsAccepted(g))).Append(' ').Append(IssueText.Label(g).FormatHTML())
                      .Append(" · ").Append(IssueText.FileList(g.Files).FormatHTML()).Append('\n');
            }
            var fatal = b.Files.Where(f => f.IsFatal).ToList();
            if (fatal.Count > 0)
            {
                sb.Append("\n<b>Cannot be uploaded</b>\n");
                foreach (var f in fatal)
                    sb.Append("⛔ ").Append($"{f.FileName}: {IssueText.FatalReason(f)}".FormatHTML()).Append('\n');
            }
            var fresh = b.Files.Where(f => f.IsNewLanguage && !f.IsFatal).ToList();
            if (fresh.Count > 0)
            {
                sb.Append("\n<b>New languages</b> (name and base cannot be changed later)\n");
                foreach (var f in fresh)
                    sb.Append("🆕 ").Append($"{f.FileName}: {IssueText.Language(f)}".FormatHTML()).Append('\n');
            }
            sb.Append('\n').Append(SelectedLine(b));

            var clean = b.Clean().Count;
            var selected = b.Selected();
            var rows = new List<InlineKeyboardButton[]>();
            var upload = new List<InlineKeyboardButton>();
            if (clean > 0)
                upload.Add(Button($"✅ Upload clean ({clean})", b, Ops.Ask, (int)BatchAction.UploadClean, KeyboardButtonStyle.Success));
            if (selected.Count > 0 && !selected.SequenceEqual(b.Clean()))
                upload.Add(Button($"⬆️ Upload selected ({selected.Count})", b, Ops.Ask, (int)BatchAction.UploadSelected, KeyboardButtonStyle.Success));
            if (upload.Count > 0) rows.Add(upload.ToArray());
            var browse = new List<InlineKeyboardButton>();
            if (groups.Count > 0) browse.Add(Button($"⚠️ Warnings ({groups.Count})", b, Ops.Issues));
            browse.Add(Button($"📄 Files ({b.Files.Count})", b, Ops.Files));
            rows.Add(browse.ToArray());
            rows.Add(new[] { Button("📝 Full report", b, Ops.Report), Button("🗑 Discard all", b, Ops.Ask, (int)BatchAction.Discard, KeyboardButtonStyle.Danger) });
            rows.Add(new[] { Button("Close", b, Ops.Close) });
            return new Screen { Text = Fit(sb.ToString()), Markup = new InlineKeyboardMarkup(rows) };
        }

        internal static Screen Issues(LanguageBatch b, int page)
        {
            var groups = b.IssueGroups();
            page = ClampPage(page, groups.Count);
            var sb = new StringBuilder("<b>Warnings</b>\n")
                .Append("Accept a warning to let it through in every file that has it. A file is selected once all its warnings are accepted, or when you pick it under Files.\n\n");
            foreach (var meaning in groups.Select(g => g.Level).Distinct().Select(IssueText.Meaning).Where(m => m != null))
                sb.Append("• ").Append(meaning.FormatHTML()).Append('\n');
            sb.Append('\n');

            var rows = new List<InlineKeyboardButton[]>();
            for (var i = page * PageSize; i < Math.Min(groups.Count, (page + 1) * PageSize); i++)
            {
                var g = groups[i];
                sb.Append(Check(b.IsAccepted(g))).Append(' ').Append(IssueText.Label(g).FormatHTML())
                  .Append(": ").Append(IssueText.FileList(g.Files, 5).FormatHTML()).Append('\n');
                rows.Add(new[] { Button($"{Check(b.IsAccepted(g))} {Short(IssueText.Label(g))} ({g.Files.Count})", b, Ops.ToggleIssue, i) });
            }
            sb.Append('\n').Append(SelectedLine(b));
            rows.AddRange(Footer(b, Ops.Issues, page, groups.Count));
            return new Screen { Text = Fit(sb.ToString()), Markup = new InlineKeyboardMarkup(rows) };
        }

        internal static Screen Files(LanguageBatch b, int page)
        {
            page = ClampPage(page, b.Files.Count);
            var sb = new StringBuilder("<b>Files</b>\nTap a file to pick or drop it by hand.\n\n");
            var rows = new List<InlineKeyboardButton[]>();
            for (var i = page * PageSize; i < Math.Min(b.Files.Count, (page + 1) * PageSize); i++)
            {
                var f = b.Files[i];
                var mark = f.IsFatal ? "⛔" : Check(b.IsSelected(f));
                var warnings = f.Warnings.Count();
                sb.Append(mark).Append(' ').Append(f.FileName.FormatHTML());
                if (f.IsFatal)
                    sb.Append(": ").Append(IssueText.FatalReason(f).FormatHTML());
                else
                {
                    if (warnings > 0) sb.Append($" · ⚠️{warnings}");
                    sb.Append($" · missing {f.Missing}");
                    sb.Append(f.IsNewLanguage ? " · 🆕 " + IssueText.Language(f).FormatHTML() : $" (live {f.LiveMissing})");
                }
                sb.Append('\n');
                rows.Add(new[] { Button($"{mark} {Short(f.FileName)}{(warnings > 0 && !f.IsFatal ? $" ⚠️{warnings}" : "")}", b, Ops.ToggleFile, i) });
            }
            sb.Append('\n').Append(SelectedLine(b));
            rows.AddRange(Footer(b, Ops.Files, page, b.Files.Count));
            return new Screen { Text = Fit(sb.ToString()), Markup = new InlineKeyboardMarkup(rows) };
        }

        /// <summary>The files an action would upload (empty for discard).</summary>
        internal static List<BatchFile> FilesFor(LanguageBatch b, BatchAction action) => action switch
        {
            BatchAction.UploadClean => b.Clean(),
            BatchAction.UploadSelected => b.Selected(),
            _ => new List<BatchFile>()
        };

        internal static Screen Confirm(LanguageBatch b, BatchAction action)
        {
            if (action == BatchAction.Discard)
                return new Screen
                {
                    Text = $"Discard the whole upload ({IssueText.Files(b.Files.Count)})? Nothing goes live.",
                    Markup = Keyboard(new[] { Button("🗑 Discard", b, Ops.Do, (int)action, KeyboardButtonStyle.Danger), Button("Back", b, Ops.Summary) })
                };
            var files = FilesFor(b, action);
            var sb = new StringBuilder($"Upload {IssueText.Files(files.Count)} live?\n");
            foreach (var f in files)
            {
                sb.Append("• ").Append(f.FileName.FormatHTML());
                var warnings = f.Warnings.Count();
                if (warnings > 0) sb.Append($" (⚠️{warnings} let through)");
                if (f.IsNewLanguage) sb.Append(" 🆕");
                sb.Append('\n');
            }
            return new Screen
            {
                Text = Fit(sb.ToString()),
                Markup = Keyboard(new[] { Button($"Yes, upload {files.Count}", b, Ops.Do, (int)action, KeyboardButtonStyle.Success), Button("Back", b, Ops.Summary) })
            };
        }

        /// <summary>The menu once the batch is decided: no buttons left.</summary>
        internal static Screen Done(LanguageBatch b) => new Screen { Text = Header(b) + "\n" + b.Outcome.FormatHTML() };

        // ---- parts ----

        private static string Header(LanguageBatch b) =>
            "<b>Language upload</b> · " + (b.Files.Count == 1 ? b.Files[0].FileName.FormatHTML() : IssueText.Files(b.Files.Count));

        private static string Counts(LanguageBatch b)
        {
            var clean = b.Files.Count(f => f.IsClean);
            var fatal = b.Files.Count(f => f.IsFatal);
            var warned = b.Files.Count - clean - fatal;
            var parts = new List<string>();
            if (clean > 0) parts.Add($"✅ {clean} clean");
            if (warned > 0) parts.Add($"⚠️ {warned} with warnings");
            if (fatal > 0) parts.Add($"⛔ {fatal} cannot be uploaded");
            return string.Join(" · ", parts);
        }

        private static string SelectedLine(LanguageBatch b) => $"Selected: {b.Selected().Count} of {b.Files.Count}";

        private static IEnumerable<InlineKeyboardButton[]> Footer(LanguageBatch b, string op, int page, int count)
        {
            var nav = new List<InlineKeyboardButton>();
            if (page > 0) nav.Add(Button("‹", b, op, page - 1));
            nav.Add(Button("Back", b, Ops.Summary));
            if ((page + 1) * PageSize < count) nav.Add(Button("›", b, op, page + 1));
            yield return nav.ToArray();
            var selected = b.Selected().Count;
            if (selected > 0)
                yield return new[] { Button($"⬆️ Upload selected ({selected})", b, Ops.Ask, (int)BatchAction.UploadSelected, KeyboardButtonStyle.Success) };
        }

        /// <summary>Which page an index is on, for re-showing a list after a toggle.</summary>
        internal static int PageOf(int index) => Math.Max(0, index) / PageSize;

        private static int ClampPage(int page, int count) =>
            Math.Max(0, Math.Min(page, (Math.Max(count, 1) - 1) / PageSize));

        private static string Check(bool on) => on ? "☑" : "☐";

        private static string Short(string s, int max = 40) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        private static InlineKeyboardButton Button(string text, LanguageBatch b, string op, int arg = 0, KeyboardButtonStyle? style = null)
        {
            var button = InlineKeyboardButton.WithCallbackData(text, Data(b.Id, op, arg));
            button.Style = style;
            return button;
        }

        private static InlineKeyboardMarkup Keyboard(InlineKeyboardButton[] row) => new InlineKeyboardMarkup(row);

        /// <summary>Cuts whole lines off the end so the text stays within Telegram's limit.</summary>
        private static string Fit(string text)
        {
            if (text.Length <= MaxText) return text;
            var cut = text.LastIndexOf('\n', MaxText - 40);
            return text.Substring(0, cut > 0 ? cut : MaxText - 40) + "\n… (more in the full report)";
        }
    }
}
