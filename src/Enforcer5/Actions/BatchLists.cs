using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Enforcer5.Actions
{
    /// <summary>
    /// The members of a /batch by id, as its preview and its log channel post list them (Daniel, 2026-11-03: the log
    /// said how many, never who). A Telegram message holds 4096 characters, about 300 ids; a batch holds up to
    /// <see cref="BatchPlan.MaxMembers"/>. So each list shows the ids that fit and "…and N more", and when any list
    /// was cut the whole of it goes along as a CSV file.
    /// </summary>
    internal static class BatchLists
    {
        /// <summary>Room for the lists in the preview, below Telegram's 4096 with the preview's own text.</summary>
        internal static int PreviewBudget = 3300;

        /// <summary>Room for the lists in the log post (settable for tests, as is the preview's), below <see cref="Helpers.LogQueue.MaxLength"/> with the line itself.</summary>
        internal static int LogBudget = 3000;

        /// <summary>One list: the string key that labels it ("Done: {0}") and its ids, in list order.</summary>
        internal sealed record Section(string Key, IReadOnlyList<long> Ids);

        /// <summary>
        /// One line per list that has anyone in it, <paramref name="line"/>(key, ids) each, together at most about
        /// <paramref name="budget"/> characters: a list that does not fit ends with <paramref name="more"/>(how many
        /// were left out), and <paramref name="cut"/> says whether any did. Ids are shared out in list order, so
        /// the first lists (those acted on) come out whole first.
        /// </summary>
        internal static string Lines(IEnumerable<Section> sections, int budget, Func<string, string, string> line,
            Func<int, string> more, out bool cut)
        {
            cut = false;
            var text = new StringBuilder();
            foreach (var section in sections.Where(s => s.Ids.Count > 0))
            {
                var (ids, left) = Join(section.Ids, budget - text.Length);
                if (left > 0)
                {
                    cut = true;
                    ids = ids.Length == 0 ? more(left) : $"{ids} {more(left)}";
                }
                text.Append('\n').Append(line(section.Key, ids));
            }
            return text.ToString();
        }

        /// <summary>The ids, comma separated, as many as fit in <paramref name="budget"/> characters; and how many did not.</summary>
        internal static (string Text, int Left) Join(IReadOnlyList<long> ids, int budget)
        {
            var text = new StringBuilder();
            var shown = 0;
            foreach (var id in ids)
            {
                var next = (shown == 0 ? "" : ", ") + id;
                if (text.Length + next.Length > budget) break;
                text.Append(next);
                shown++;
            }
            return (text.ToString(), ids.Count - shown);
        }

        /// <summary>A CSV file (UTF-8, header first), its fields quoted where they need it.</summary>
        internal static byte[] Csv(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows)
        {
            var csv = new StringBuilder();
            foreach (var row in new[] { header }.Concat(rows))
                csv.Append(string.Join(",", row.Select(Field))).Append('\n');
            return Encoding.UTF8.GetBytes(csv.ToString());
        }

        private static string Field(string value)
        {
            value ??= "";
            return value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";
        }
    }
}
