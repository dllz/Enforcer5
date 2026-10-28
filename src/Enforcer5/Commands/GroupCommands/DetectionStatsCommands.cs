using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Enforcer5.Attributes;
using Enforcer5.Data;
using Enforcer5.Detection;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5
{
    /// <summary>
    /// How Detection is doing (design section 11, build step 13): /detectstats for a group's admins, /detectreview and
    /// /simtest for the developers. Counts only, never names or text.
    /// </summary>
    public static partial class Commands
    {
        internal const int StatsDays = 30;

        /// <summary>/detectstats: each sign over the last 30 days in this group and what the admins decided after, in private.</summary>
        [Command(Trigger = "detectstats", InGroupOnly = true, GroupAdminOnly = true)]
        public static void DetectStats(Update update, string[] args)
        {
            var message = update.Message;
            var chatId = message.Chat.Id;
            var lang = Methods.GetGroupLanguage(message, true).Doc;
            if (!PostgresStorage.Ready)
            {
                Bot.SendReply(Methods.GetLocaleString(lang, "failed"), update);
                return;
            }
            IReadOnlyList<SignStat> signs;
            (long Missed, long Decisions) missed;
            var since = new DateTimeOffset(Clock.UtcNow.AddDays(-StatsDays), TimeSpan.Zero);
            try
            {
                signs = Repositories.DetectionLog.SignRecordAsync(chatId, since).GetAwaiter().GetResult();
                missed = Repositories.DetectionLog.MissedAsync(chatId, since).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"/detectstats in {chatId}");
                Bot.SendReply(Methods.GetLocaleString(lang, "failed"), update);
                return;
            }

            var title = DetectionReports.TitleAsync(chatId).GetAwaiter().GetResult();
            var text = new StringBuilder(Methods.GetLocaleStringNoFormat(lang, "detectStatsHeader", title.FormatHTML(), StatsDays));
            text.Append("\n\n");
            var shown = signs.Where(s => s.Hits > 0).ToList();
            if (shown.Count == 0) text.Append(Methods.GetLocaleString(lang, "detectStatsNone"));
            foreach (var s in shown)
            {
                text.Append("• ").Append(DetectionReports.SignLabel(lang, s.Sign).FormatHTML()).Append(": ")
                    .Append(Methods.GetLocaleString(lang, "detectStatsLine", s.Hits, s.Members, s.Removed, s.Muted, s.Fined + s.Undone));
                if (s.NearMisses > 0) text.Append(' ').Append(Methods.GetLocaleString(lang, "detectStatsNear", s.NearMisses));
                text.Append('\n');
            }
            if (missed.Decisions > 0)
                text.Append('\n').Append(Methods.GetLocaleString(lang, "detectStatsMissed", missed.Missed, missed.Decisions));

            var sent = Bot.Send(text.ToString().TrimEnd(), message.From.Id);
            Bot.SendReply(Methods.GetLocaleString(lang, sent != null ? "filterTestSent" : "filterTestStartMe"), update);
        }

        /// <summary>
        /// /detectreview [days]: every sign across all groups (default 30 days): how often, what admins decided after, how
        /// sure it is, its near misses, and the comparing signs by similarity. For tuning; developers only.
        /// </summary>
        [Command(Trigger = "detectreview", DevOnly = true, Description = "Detection: every sign's record across all groups")]
        public static void DetectReview(Update update, string[] args)
        {
            if (!PostgresStorage.Ready)
            {
                Bot.SendReply("PostgreSQL is not ready.", update);
                return;
            }
            var days = int.TryParse(args.Length > 1 ? args[1]?.Trim() : null, out var d) ? Math.Clamp(d, 1, 730) : StatsDays;
            var since = new DateTimeOffset(Clock.UtcNow.AddDays(-days), TimeSpan.Zero);
            try
            {
                var signs = Repositories.DetectionLog.SignRecordAsync(null, since).GetAwaiter().GetResult();
                var missed = Repositories.DetectionLog.MissedAsync(null, since).GetAwaiter().GetResult();
                var buckets = Repositories.DetectionLog.SimilarityBucketsAsync(since).GetAwaiter().GetResult();
                Bot.SendReply(Review(signs, missed, buckets, days), update);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "/detectreview");
                Bot.SendReply("Failed; see the error chat.", update);
            }
        }

        /// <summary>The /detectreview text; pure, for tests.</summary>
        internal static string Review(IReadOnlyList<SignStat> signs, (long Missed, long Decisions) missed,
            IReadOnlyList<SimilarityBucket> buckets, int days)
        {
            var text = new StringBuilder($"<b>Detection, all groups, last {days} days</b>\n");
            text.Append("sign: hits / members / groups · removed, muted, fine, undone · sure · near misses\n\n");
            foreach (var s in signs)
            {
                var decided = s.Removed + s.Muted + s.Fined + s.Undone;
                var sure = decided == 0 ? "–" : $"{Math.Round(100.0 * (s.Removed + s.Muted) / decided)}%";
                text.Append($"<code>{s.Sign.FormatHTML()}</code>: {s.Hits}/{s.Members}/{s.Groups} · {s.Removed}, {s.Muted}, {s.Fined}, {s.Undone} · {sure}");
                if (s.NearMisses > 0) text.Append($" · {s.NearMisses}");
                text.Append('\n');
            }
            if (signs.Count == 0) text.Append("Nothing recorded.\n");
            text.Append($"\nAdmins' removals, mutes and deletes with no sign before: {missed.Missed} of {missed.Decisions}.\n");
            if (buckets.Count > 0)
            {
                text.Append("\n<b>By similarity</b> (count, then bad/fine after)\n");
                foreach (var sign in buckets.GroupBy(b => b.Sign))
                {
                    text.Append($"<code>{sign.Key.FormatHTML()}</code>\n");
                    foreach (var b in sign.OrderByDescending(b => b.From))
                        text.Append($"  {b.From.ToString("0.0", CultureInfo.InvariantCulture)}+ {b.Kind}: {b.Count} ({b.Bad}/{b.Fine})\n");
                }
            }
            text.Append("\nThresholds: ").Append(string.Join(", ", SimilarityTuning.Current().Select(t => $"{t.Name}={t.Value}")));
            return text.ToString();
        }

        /// <summary>/simtest a | b: how alike two texts or names are, by the thresholds the checks use. Developers only.</summary>
        [Command(Trigger = "simtest", DevOnly = true, Description = "Detection: how alike two texts or names are")]
        public static void SimTest(Update update, string[] args)
        {
            var input = args.Length > 1 ? args[1] : null;
            var parts = input?.Split('|', 2);
            if (parts == null || parts.Length < 2 || parts.Any(string.IsNullOrWhiteSpace))
            {
                Bot.SendReply("/simtest first text or name | second text or name", update);
                return;
            }
            Bot.SendReply(Compare(parts[0].Trim(), parts[1].Trim()), update);
        }

        /// <summary>The /simtest text; pure, for tests.</summary>
        internal static string Compare(string a, string b)
        {
            var na = MessageFingerprint.Normalize(a);
            var nb = MessageFingerprint.Normalize(b);
            var text = SimilarityTuning.TextSimilarity(a, b);
            var name = SimilarityTuning.NameSimilarity(na, nb);
            var person = new User { Id = 1, FirstName = a };
            var target = new User { Id = 2, FirstName = b };
            var like = NameSigns.Impersonates(person, new[] { (target, "admin") });
            static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
            string Verdict(double v, double match, double near) => v >= match ? "same" : v >= near ? "near miss" : "different";
            return string.Join("\n",
                $"Normalised: <code>{na.FormatHTML()}</code> | <code>{nb.FormatHTML()}</code>",
                $"Text: {F(text)} → {Verdict(text, SimilarityTuning.Text, SimilarityTuning.TextNear)} (same from {F(SimilarityTuning.Text)}, near from {F(SimilarityTuning.TextNear)})",
                $"Name: {F(name)} → {(like != null ? $"copies it ({like.How})" : name >= SimilarityTuning.NameNear ? "near miss" : "different")}" +
                $" (near from {F(SimilarityTuning.NameNear)})",
                $"Staff word: {(NameSigns.HasKeyword(person) ? "first" : "")}{(NameSigns.HasKeyword(person) && NameSigns.HasKeyword(target) ? ", " : "")}{(NameSigns.HasKeyword(target) ? "second" : "")}{(!NameSigns.HasKeyword(person) && !NameSigns.HasKeyword(target) ? "none" : "")}");
        }
    }
}

namespace Enforcer5
{
    public static partial class Commands
    {
        /// <summary>
        /// /channelrep &lt;@name|id|website&gt;: how the members who pointed at a channel or a website turned out, across groups
        /// (Detection step 14). Counts only. Developers only.
        /// </summary>
        [Command(Trigger = "channelrep", DevOnly = true, Description = "Detection: a channel's or website's reputation")]
        public static void ChannelRep(Update update, string[] args)
        {
            var input = (args.Length > 1 ? args[1] : null)?.Trim();
            if (string.IsNullOrEmpty(input))
            {
                Bot.SendReply("/channelrep @channel, a channel id, or a website", update);
                return;
            }
            if (!PostgresStorage.Ready)
            {
                Bot.SendReply("PostgreSQL is not ready.", update);
                return;
            }
            try
            {
                var (kind, value, label) = ResolvePointer(input);
                if (value == null)
                {
                    Bot.SendReply($"No public channel or website found for {input.FormatHTML()}.", update);
                    return;
                }
                var row = Repositories.DetectionData.GetReputationAsync(kind, value).GetAwaiter().GetResult();
                Bot.SendReply(ReputationText(label, row), update);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "/channelrep");
                Bot.SendReply("Failed; see the error chat.", update);
            }
        }

        private static (string Kind, string Value, string Label) ResolvePointer(string input)
        {
            if (long.TryParse(input, out var id)) return ("channel", id.ToString(), id.ToString());
            var name = input.StartsWith('@') ? input[1..] : ProfileSigns.TelegramName(input);
            if (name != null)
            {
                var channel = Profiles.AwaitAsync(Profiles.ChannelAsync(0, name)).GetAwaiter().GetResult();
                return channel == null ? ("channel", null, input) : ("channel", channel.Id.ToString(), $"@{name} ({channel.Id})");
            }
            var domain = Reputation.DomainOf(input.Contains("://") ? input : "https://" + input);
            return ("domain", domain, domain);
        }

        /// <summary>The /channelrep text; pure, for tests.</summary>
        internal static string ReputationText(string label, ReputationRow row)
        {
            if (row == null) return $"{label.FormatHTML()}: no new member has pointed at it (in the data kept).";
            var bad = row.BadMembers >= Reputation.MinBad && row.BadGroups >= Reputation.MinGroups &&
                      row.FineMembers * Reputation.FinePerBad <= row.BadMembers;
            return $"<b>{label.FormatHTML()}</b>\n" +
                   $"Members who pointed at it, removed or muted for spam or abuse: {row.BadMembers} in {row.BadGroups} groups\n" +
                   $"Found fine: {row.FineMembers}\n" +
                   $"Groups it was seen in: {row.Groups}, last {row.LastSeenAt:yyyy-MM-dd}\n" +
                   $"Reputation: {(bad ? "bad (new members pointing at it get the sign)" : "not bad")}, counted {row.ComputedAt:yyyy-MM-dd HH:mm} UTC";
        }
    }
}
