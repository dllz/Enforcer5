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
    internal sealed record SignHit(string Sign, string Evidence = null, int? Weight = null, bool Trial = false);

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
                var policy = await DetectionSettings.GetAsync(chatId);
                var settings = policy.Engine.For(sign.Name);
                if (!settings.Enabled) return null;

                var member = await MemberStateAsync(chatId, userId);
                if (member.Trust is TrustLevel.Admin or TrustLevel.Approved) return null;
                if (member.Trust == TrustLevel.Established)
                {
                    if (sign.NewcomersOnly) return null;
                    if (Methods.IsGroupAdmin(userId, chatId)) return null;
                }
                else if (Methods.IsGroupAdmin(userId, chatId)) return null;

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
                var deleted = settings.Delete || action == "delete";
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
                    Actions.Execute(new DetectionAction(chatId, userId, message?.MessageId, sign.Name, action, deleted, reported));

                var fingerprint = message != null ? MessageFingerprint.Of(message) : null;
                DetectionRecorder.Add(new DetectionRecord(chatId, userId, sign.Name, sign.Phase == SignPhase.Inline ? "inline" : "enriched",
                    weight, change.Before, change.After, action, deleted, reported, mode, message?.MessageId,
                    fingerprint?.ContentType, fingerprint?.TextHash, fingerprint?.MediaUniqueId,
                    member.Trust.ToString().ToLowerInvariant(), member.Messages, member.Since, hit.Evidence,
                    BanHistory.Edition, now));
                return outcome;
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Detection of {hit?.Sign} for {userId} in {chatId}");
                return null;
            }
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
                    stored = await Repositories.DetectionLog.RecentHitsAsync(chatId, userId, Clock.UtcNow - MemberScores.Window);
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
            DetectionRecorder.ResetForTests();
        }
    }
}
