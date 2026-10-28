using System;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using StackExchange.Redis;
using Telegram.Bot.Types;

namespace Enforcer5.Detection
{
    /// <summary>A sign a check saw. <see cref="Evidence"/> is JSON of ids only, never text.</summary>
    /// <param name="Trial">From a rule on trial: recorded, never acted on, and not added to the score.</param>
    internal sealed record SignHit(string Sign, string Evidence = null, int? Weight = null, bool Trial = false)
    {
        /// <summary>What a ported check already did; the engine records it and does nothing itself.</summary>
        internal CheckDecision Done { get; init; }

        /// <summary>
        /// When the check decided, so the record is not later than the action it took: a decision is
        /// linked to the detections from before it.
        /// </summary>
        internal DateTime? At { get; init; }

        /// <summary>What the admins' report says about it, in the group's language; never message content.</summary>
        internal string Detail { get; init; }
    }

    /// <summary>What a ported check did about a sign (<see cref="SignDefinition.Ported"/>).</summary>
    /// <param name="Action">One of <see cref="DetectionPolicy.Actions"/>; anything else is recorded as none.</param>
    internal sealed record CheckDecision(string Action, bool Deleted, bool Reported);

    /// <summary>What the engine made of a sign.</summary>
    /// <param name="Mode">live, observe or trial.</param>
    /// <param name="Action">What the group's settings say to do (done only in live mode).</param>
    internal sealed record DetectionOutcome(string Mode, string Action, bool Deleted, bool Reported, int ScoreBefore,
        int ScoreAfter)
    {
        internal bool Acted => Mode == "live";
    }

    /// <summary>
    /// The Detection engine (design sections 1 and 3, build step 3). A check reports a sign; the engine
    /// decides what it means for this group and member and records it:
    /// <list type="number">
    /// <item>the group's setting for the sign (or the catalog's default), and who the member is: admins
    /// and approved members are never acted on, established members only for signs that are not
    /// about newcomers;</item>
    /// <item>the member's score: each sign counts once a day, and crossing the group's report or mute
    /// threshold adds a report or a mute;</item>
    /// <item>observe mode: a group's first 14 days (from when the engine first sees it) record what would
    /// have been done and do nothing, except for signs live from day one;</item>
    /// <item>the action, through the enforcement queue (<see cref="IDetectionActions"/>);</item>
    /// <item>the record in <c>detections</c>, with the member's state then, for learning: the admins'
    /// decisions are linked to it (<see cref="Decisions"/>).</item>
    /// </list>
    /// Never in front of the checks that exist today: it is called from dispatched work and a failure
    /// is reported, not thrown.
    /// </summary>
    internal static class DetectionEngine
    {
        internal static IDetectionActions Actions { get; set; } = new EnforcerDetectionActions();

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<long, byte> StartingObservation = new();

        /// <summary>
        /// Handles <paramref name="hit"/> for <paramref name="userId"/> in <paramref name="chatId"/>,
        /// about <paramref name="message"/> if there is one. Null when the sign is off for the group or
        /// does not apply to the member.
        /// </summary>
        internal static async Task<DetectionOutcome> HitAsync(long chatId, long userId, Message message, SignHit hit)
        {
            try
            {
                var sign = SignCatalog.Get(hit.Sign) ?? throw new ArgumentException($"Unknown sign {hit.Sign}");
                if (sign.Ported || hit.Done != null) return await RecordCheckAsync(chatId, userId, message, sign, hit);
                var policy = await DetectionSettings.GetAsync(chatId);
                var settings = policy.Engine.For(sign.Name);
                if (!settings.Enabled) return null;

                var member = await MemberStateAsync(chatId, userId);
                if (member.Trust is TrustLevel.Admin or TrustLevel.Approved) return null;
                if (member.Trust == TrustLevel.Established)
                {
                    if (sign.NewcomersOnly || settings.SkipEstablished) return null;
                    if (Methods.IsGroupAdmin(userId, chatId)) return null;
                }
                else if (Methods.IsGroupAdmin(userId, chatId)) return null;

                // An admin marked them fine for this sign on a report.
                if (await Fines.ExemptAsync(chatId, userId, sign.Name)) return null;

                var now = Clock.UtcNow;
                if (policy.Engine.ObserveUntil == null) StartObserving(chatId);
                var mode = hit.Trial ? "trial"
                    : sign.LiveFromDayOne || !policy.Engine.Observing(now) ? "live"
                    : "observe";

                if (MemberScores.NeedsLoad(chatId, userId)) await LoadScoreAsync(chatId, userId);
                var weight = hit.Weight ?? settings.Weight;
                MemberScores.Change change;
                if (hit.Trial)
                {
                    var current = MemberScores.Of(chatId, userId);
                    change = new MemberScores.Change(current, current, false);
                }
                else change = MemberScores.Add(chatId, userId, sign.Name, weight);

                var action = settings.Action;
                var deleted = settings.Delete || action == "delete" || settings.DeleteNewcomers && member.Trust == TrustLevel.New;
                var reported = settings.Report || action == "report";
                if (change.Counted)
                {
                    if (change.Before < policy.Engine.ReportAt && change.After >= policy.Engine.ReportAt) reported = true;
                    if (policy.Engine.MuteEnabled && change.Before < policy.Engine.MuteAt && change.After >= policy.Engine.MuteAt)
                        action = Stronger(action, "mute");
                }
                deleted &= message != null;
                var outcome = new DetectionOutcome(mode, action, deleted, reported, change.Before, change.After);

                if (outcome.Acted)
                    Actions.Execute(new DetectionAction(chatId, userId, message?.MessageId, sign.Name, action, deleted, reported)
                    {
                        Member = message?.From?.Id == userId ? message.From : null,
                        Score = change.After,
                        Detail = hit.Detail
                    });

                Record(chatId, userId, message, sign, hit, weight, outcome, member, now, policy);
                return outcome;
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Detection of {hit?.Sign} for {userId} in {chatId}");
                return null;
            }
        }

        /// <summary>
        /// A ported check (<see cref="SignDefinition.Ported"/>) did <paramref name="action"/> about
        /// <paramref name="userId"/>: recorded in the background, never waited for and never in the way of
        /// what the check does, which stays the check's own (its settings, exemptions and notices). Call it
        /// once the check has decided to act, after its own exemptions.
        /// </summary>
        /// <param name="userId">Null for a post made as a channel: the member behind it cannot be seen.</param>
        /// <param name="message">The message it was about; null when there is none of theirs (a join).</param>
        /// <param name="evidence">Ids, counts and settings only, never text; serialised to JSON.</param>
        /// <param name="weight">What it adds to the member's score; null is the sign's (0 for the ported checks).</param>
        /// <param name="trial">A rule on trial: recorded as what it would have done; nothing was done and the score is left alone.</param>
        internal static void Checked(long chatId, long? userId, Message message, SignDefinition sign, string action,
            bool deleted = false, bool reported = false, object evidence = null, int? weight = null, bool trial = false)
        {
            if (!PostgresStorage.Ready) return; // nothing would be kept
            var hit = new SignHit(sign.Name, evidence == null ? null : System.Text.Json.JsonSerializer.Serialize(evidence), weight, trial)
            {
                Done = new CheckDecision(action, deleted, reported),
                At = Clock.UtcNow
            };
            Bot.Dispatch(async () => await HitAsync(chatId, userId ?? 0, message, hit), "Detection.Checked");
        }

        /// <summary>
        /// A ported check's decision, as it was made: every member it applied to, live from day one, nothing
        /// done here. The score only changes for a sign a group gave a weight.
        /// </summary>
        private static async Task<DetectionOutcome> RecordCheckAsync(long chatId, long userId, Message message,
            SignDefinition sign, SignHit hit)
        {
            var done = hit.Done ?? new CheckDecision("none", false, false);
            var policy = await DetectionSettings.GetAsync(chatId);
            var settings = policy.Engine.For(sign.Name);
            if (!settings.Enabled) return null;
            var member = userId != 0 ? await MemberStateAsync(chatId, userId) : null;
            var weight = hit.Weight ?? settings.Weight;
            MemberScores.Change change;
            if (weight > 0 && userId != 0 && !hit.Trial)
            {
                if (MemberScores.NeedsLoad(chatId, userId)) await LoadScoreAsync(chatId, userId);
                change = MemberScores.Add(chatId, userId, sign.Name, weight);
            }
            else
            {
                var current = userId != 0 ? MemberScores.Of(chatId, userId) : 0;
                change = new MemberScores.Change(current, current, false);
            }
            var action = Array.IndexOf(DetectionPolicy.Actions, done.Action) >= 0 ? done.Action : "none";
            var outcome = new DetectionOutcome(hit.Trial ? "trial" : "live", action, done.Deleted && message != null, done.Reported,
                change.Before, change.After);
            Record(chatId, userId, message, sign, hit, weight, outcome, member, hit.At ?? Clock.UtcNow, policy);
            return outcome;
        }

        private static void Record(long chatId, long userId, Message message, SignDefinition sign, SignHit hit, int weight,
            DetectionOutcome outcome, MemberState member, DateTime now, DetectionPolicy policy)
        {
            var fingerprint = message != null ? MessageFingerprint.Of(message) : null;
            DetectionRecorder.Add(new DetectionRecord(chatId, userId, sign.Name, sign.Phase == SignPhase.Inline ? "inline" : "enriched",
                weight, outcome.ScoreBefore, outcome.ScoreAfter, outcome.Action, outcome.Deleted, outcome.Reported, outcome.Mode,
                message?.MessageId, fingerprint?.ContentType, fingerprint?.TextHash, fingerprint?.MediaUniqueId,
                member?.Trust.ToString().ToLowerInvariant(), member?.Messages, member?.Since, hit.Evidence,
                BanHistory.Edition, now) { Category = SignCatalog.CategoryOf(sign.Name, policy.Filters) });
        }

        /// <summary>The stronger of two actions, in the order of <see cref="DetectionPolicy.Actions"/>.</summary>
        internal static string Stronger(string a, string b) =>
            Array.IndexOf(DetectionPolicy.Actions, b) > Array.IndexOf(DetectionPolicy.Actions, a) ? b : a;

        private sealed record MemberState(TrustLevel Trust, long? Messages, DateTimeOffset? Since);

        private static async Task<MemberState> MemberStateAsync(long chatId, long userId)
        {
            var trust = MemberTrust.OfAsync(chatId, userId);
            var counts = Redis.db.HashGetAsync($"{chatId}:users:{userId}", new RedisValue[] { "msgs", "first", "joined" });
            await Task.WhenAll(trust, counts);
            static long? Long(RedisValue[] values, int i) =>
                values != null && values.Length > i && values[i].HasValue && long.TryParse(values[i].ToString(), out var v) ? v : null;
            var since = new[] { Long(counts.Result, 1), Long(counts.Result, 2) }.Where(v => v.HasValue).Select(v => v.Value)
                .DefaultIfEmpty().Max();
            return new MemberState(trust.Result, Long(counts.Result, 0),
                since > 0 ? DateTimeOffset.FromUnixTimeSeconds(since) : null);
        }

        private static async Task LoadScoreAsync(long chatId, long userId)
        {
            var stored = Array.Empty<MemberScores.Hit>() as System.Collections.Generic.IReadOnlyList<MemberScores.Hit>;
            if (PostgresStorage.Ready)
            {
                try
                {
                    // Not from before an admin marked them fine: that score was forgiven.
                    var since = Clock.UtcNow - MemberScores.Window;
                    if (await Fines.LastFineAsync(chatId, userId) is { } fined && fined > since) since = fined;
                    stored = await Repositories.DetectionLog.RecentHitsAsync(chatId, userId, since);
                }
                catch (Exception e)
                {
                    // Their score starts from what is in memory; it is a hint, not a ledger.
                    ErrorReporter.Report(e, $"Loading the detection score of {userId} in {chatId}");
                }
            }
            MemberScores.Load(chatId, userId, stored);
        }

        /// <summary>A group the engine sees for the first time starts its observe weeks now.</summary>
        private static void StartObserving(long chatId)
        {
            if (!PostgresStorage.Ready || !StartingObservation.TryAdd(chatId, 0)) return;
            var until = new DateTimeOffset(Clock.UtcNow, TimeSpan.Zero) + EngineSettings.ObserveFor;
            Bot.Dispatch(async () =>
            {
                try
                {
                    await DetectionSettings.UpdateAsync(chatId, p => p.Engine.ObserveUntil != null
                        ? p
                        : p with { Engine = p.Engine with { ObserveUntil = until } });
                }
                catch (Exception e)
                {
                    StartingObservation.TryRemove(chatId, out _);
                    ErrorReporter.Report(e, $"Starting the observe weeks of {chatId}");
                }
            }, "DetectionEngine.StartObserving");
        }

        internal static void ResetForTests()
        {
            StartingObservation.Clear();
            MemberScores.ResetForTests();
            Fines.ResetForTests();
            TrustOverrides.ResetForTests();
            MessageSigns.ResetForTests();
            NameSigns.ResetForTests();
            DetectionReports.ResetForTests();
            DetectionRecorder.ResetForTests();
            GroupBots.ResetForTests();
        }
    }
}
