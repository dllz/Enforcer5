using System;
using System.Linq;
using Enforcer5.Actions;
using Enforcer5.Data;
using Enforcer5.Detection;
using Enforcer5.Enforcement;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;

namespace Enforcer5.Tests.Actions
{
    /// <summary>The action seam's types without Telegram: where an action came from, and the job it becomes.</summary>
    [TestClass]
    public class ActionSeamTests
    {
        private static Update Command() => new() { Message = new Message { Text = "/ban" } };
        private static Update Button(string data) => new() { CallbackQuery = new CallbackQuery { Data = data, From = new User { Id = 7 } } };

        /// <summary>What <c>Decisions</c> stored before the seam took over working it out.</summary>
        private static string OldLabel(Update update, long? actorId, bool fromReport)
        {
            var buttonSource = Decisions.ButtonSource(update?.CallbackQuery?.Data);
            return fromReport ? "report"
                : actorId == null ? "auto"
                : buttonSource != null ? buttonSource
                : update?.CallbackQuery != null ? "button"
                : "command";
        }

        [TestMethod]
        public void TheLabels_AreTheOnesDecisionsAlwaysStored()
        {
            var updates = new[]
            {
                null, Command(), Button("banflag:-100:1:2"), Button($"{DetectionReports.Trigger}:-100:1:2:ban"),
                Button("raid:-100:ban:abc"), Button("raidlink:-100:ban:1:2"), Button("userbuttonbanuser:-100:2")
            };
            foreach (var update in updates)
            foreach (var actor in new long?[] { null, 5 })
            foreach (var fromReport in new[] { false, true })
                Assert.AreEqual(OldLabel(update, actor, fromReport),
                    ActionSources.Label(ActionSources.Derive(update, actor, fromReport)),
                    $"{update?.CallbackQuery?.Data ?? update?.Message?.Text ?? "no update"}, actor {actor}, report {fromReport}");
        }

        [TestMethod]
        public void Derive_NamesTheSource()
        {
            Assert.AreEqual(ActionSource.Command, ActionSources.Derive(Command(), 5, false));
            Assert.AreEqual(ActionSource.Button, ActionSources.Derive(Button("userbuttonbanuser:-100:2"), 5, false));
            Assert.AreEqual(ActionSource.Report, ActionSources.Derive(Button("banflag:-100:1:2"), 5, true));
            Assert.AreEqual(ActionSource.Report, ActionSources.Derive(Button($"{DetectionReports.Trigger}:-100:1:2:ban"), 5, false));
            Assert.AreEqual(ActionSource.Raid, ActionSources.Derive(Button("raidlink:-100:ban:1:2"), 5, false));
            Assert.AreEqual(ActionSource.Auto, ActionSources.Derive(Command(), null, false));
        }

        [TestMethod]
        public void TheBotsOwnSources_AreLabelledAuto()
        {
            foreach (var source in new[] { ActionSource.Auto, ActionSource.Escalation, ActionSource.Detection, ActionSource.Filter,
                         ActionSource.Flood, ActionSource.Limits, ActionSource.GlobalBan, ActionSource.MuteOnJoin, ActionSource.Expiry, ActionSource.ChannelPost })
                Assert.AreEqual("auto", ActionSources.Label(source), source.ToString());
        }

        private static ActionRequest Request(ActionKind kind, ActionSource source = ActionSource.Command, DateTime? until = null) => new()
        {
            Kind = kind, ChatId = -100, TargetId = 2, ActorId = 5, Source = source, Reason = "why", UntilUtc = until
        };

        [TestMethod]
        public void EachKind_BecomesTheJobTheHelpersSubmitted()
        {
            var until = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
            var cases = new (ActionRequest Request, EnforcementKind Kind, DateTime? Until)[]
            {
                (Request(ActionKind.Ban), EnforcementKind.Ban, null),
                (Request(ActionKind.TempBan, until: until), EnforcementKind.Ban, until),
                (Request(ActionKind.Unban), EnforcementKind.Unban, null),
                (Request(ActionKind.Kick), EnforcementKind.Kick, null),
                (Request(ActionKind.Mute), EnforcementKind.Mute, null),
                (Request(ActionKind.TempMute, until: until), EnforcementKind.Mute, until),
                (Request(ActionKind.Unmute), EnforcementKind.Unmute, null),
                (Request(ActionKind.BanChannel), EnforcementKind.BanSenderChat, null),
                (Request(ActionKind.UnbanChannel), EnforcementKind.UnbanSenderChat, null),
            };
            // Plus Delete, below.
            Assert.AreEqual(Enum.GetValues<ActionKind>().Count(k => !WarnActions.Handles(k)) - 1, cases.Length, "every kind is covered");
            foreach (var (request, kind, expectedUntil) in cases)
            {
                var job = ActionSeam.ToJob(request);
                Assert.AreEqual((kind, -100L, 2L, expectedUntil), (job.Kind, job.ChatId, job.TargetId, job.UntilUtc), request.Kind.ToString());
                Assert.AreEqual(("why", (long?)5, true, false, BanEnd.Unbanned),
                    (job.Note.Reason, job.Note.ActorId, job.Note.Record, job.Note.NewJoiner, job.Note.End), request.Kind.ToString());
            }
        }

        [TestMethod]
        public void TheJob_CarriesWhatTheHelpersPassed()
        {
            var trigger = new WarningRef();
            var report = new ReportRef();
            var ban = ActionSeam.ToJob(Request(ActionKind.Ban) with { RevokeMessages = true, Trigger = trigger, Report = report });
            Assert.IsTrue(ban.RevokeMessages);
            Assert.AreSame(trigger, ban.Trigger);
            Assert.AreSame(report, ban.Report);

            Assert.IsFalse(ActionSeam.ToJob(Request(ActionKind.Kick) with { Record = false }).Note.Record, "/kickme");
            Assert.AreEqual(BanEnd.Expired, ActionSeam.ToJob(Request(ActionKind.Unban, ActionSource.Expiry)).Note.End);
            Assert.IsTrue(ActionSeam.ToJob(Request(ActionKind.Mute, ActionSource.MuteOnJoin)).Note.NewJoiner);
            Assert.IsFalse(ActionSeam.ToJob(Request(ActionKind.Mute, ActionSource.Detection)).Note.NewJoiner);
            Assert.IsTrue(ActionSeam.ToJob(Request(ActionKind.TempMute, ActionSource.Raid, DateTime.UtcNow) with { NewJoiner = true }).Note.NewJoiner,
                "raid quarantine");
        }

        [TestMethod]
        public void ADelete_IsADeleteJobForTheMessage_NotForTheMember()
        {
            var job = ActionSeam.ToJob(Request(ActionKind.Delete) with { MessageId = 77 });
            Assert.AreEqual((EnforcementKind.Delete, -100L, 0L), (job.Kind, job.ChatId, job.TargetId),
                "no member: the queue's known-admin refusal is not for deletes");
            CollectionAssert.AreEqual(new[] { 77 }, job.MessageIds);
        }

        [TestMethod]
        public void WarnKinds_AreCarriedOutWithoutTheQueue()
        {
            foreach (var kind in new[] { ActionKind.Warn, ActionKind.Prewarn, ActionKind.MediaStrike, ActionKind.RemoveWarn, ActionKind.ResetWarns })
            {
                Assert.IsTrue(WarnActions.Handles(kind), kind.ToString());
                Assert.ThrowsException<ArgumentOutOfRangeException>(() => ActionSeam.ToJob(Request(kind)), kind.ToString());
            }
        }

        [TestMethod]
        public void NothingToDo_IsNeitherDoneNorRefused()
        {
            var result = new ActionResult(Request(ActionKind.RemoveWarn), EnforcementResult.Done) { Nothing = true };
            Assert.AreEqual((ActionOutcome.NothingToDo, false), (result.Outcome, result.Succeeded));
        }

        [DataTestMethod]
        [DataRow("123 appeal accepted", "123", "appeal accepted")]
        [DataRow("@someone", "@someone", null)]
        [DataRow("  @someone   wrong user ", "@someone", "wrong user")]
        [DataRow("appeal accepted", null, "appeal accepted")]
        [DataRow("@", null, "@")]
        [DataRow("-5 x", null, "-5 x")]
        [DataRow(null, null, null)]
        public void RemoveWarn_ReadsTheMemberNamedFirst(string text, string target, string after)
        {
            Assert.AreEqual((target, after), Commands.SplitTarget(text));
        }

        [TestMethod]
        public void ATempActionWithoutAnEnd_IsRefused()
        {
            Assert.ThrowsException<ArgumentException>(() => ActionSeam.ToJob(Request(ActionKind.TempBan)));
            Assert.ThrowsException<ArgumentException>(() => ActionSeam.ToJob(Request(ActionKind.TempMute)));
        }

        [TestMethod]
        public void TheOutcome_FollowsTheQueue()
        {
            var request = Request(ActionKind.Ban);
            Assert.AreEqual(ActionOutcome.Done, new ActionResult(request, EnforcementResult.Done).Outcome);
            Assert.AreEqual(ActionOutcome.Queued, new ActionResult(request, EnforcementResult.Queued).Outcome);
            var refused = new ActionResult(request, EnforcementResult.Refuse(Refusal.TargetIsAdmin));
            Assert.AreEqual((ActionOutcome.Refused, Refusal.TargetIsAdmin, false), (refused.Outcome, refused.Refusal, refused.Succeeded));
            Assert.IsTrue(new ActionResult(request, EnforcementResult.Queued).Succeeded);
        }

        /// <summary>
        /// Section 12 of the design: every ban, kick, mute, unban, unmute and channel ban goes through the seam,
        /// so nothing outside <c>Actions/</c> and <c>Enforcement/</c> builds those jobs. Routine deletes and raid
        /// mode's lock of the group (SetPermissions) are not actions on a member and stay direct.
        /// </summary>
        [TestMethod]
        public void OnlyTheSeam_BuildsJobsThatActOnAMember()
        {
            var pattern = new System.Text.RegularExpressions.Regex(
                @"EnforcementJob\s*\.\s*(Ban|Kick|Mute|Unban|Unmute|BanSenderChat|UnbanSenderChat)\s*\(");
            var root = TestEnvironment.SourceRoot;
            var offenders = System.IO.Directory.GetFiles(root, "*.cs", System.IO.SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{System.IO.Path.DirectorySeparatorChar}obj{System.IO.Path.DirectorySeparatorChar}")
                            && !f.Contains($"{System.IO.Path.DirectorySeparatorChar}bin{System.IO.Path.DirectorySeparatorChar}"))
                .Select(f => (File: System.IO.Path.GetRelativePath(root, f), Text: System.IO.File.ReadAllText(f)))
                .Where(f => !f.File.StartsWith("Actions") && !f.File.StartsWith("Enforcement") && pattern.IsMatch(f.Text))
                .Select(f => f.File).ToArray();
            Assert.AreEqual(0, offenders.Length, "Act on members through ActionSeam: " + string.Join(", ", offenders));
        }

        /// <summary>Section 5 of the design: which sources hooks may block or change, and which they hear about.</summary>
        [TestMethod]
        public void TheHooks_RunOnTheSourcesOfSectionFive()
        {
            var before = new[] { ActionSource.Command, ActionSource.Button, ActionSource.Report, ActionSource.Escalation };
            var neither = new[] { ActionSource.Flood, ActionSource.Raid, ActionSource.GlobalBan, ActionSource.MuteOnJoin };
            foreach (var source in Enum.GetValues<ActionSource>())
            {
                Assert.AreEqual(before.Contains(source), ActionHooks.BeforeRuns(source), $"before, {source}");
                Assert.AreEqual(!neither.Contains(source), ActionHooks.AfterRuns(source), $"after, {source}");
            }
        }

        [TestMethod]
        public void ABlockedResult_SaysWhoAndWhy_AndIsNoSuccess()
        {
            var blocked = new ActionResult(Request(ActionKind.Ban),
                EnforcementResult.Refuse(Refusal.Blocked, new ActionBlockedException("rule 7", "Not today")));
            Assert.AreEqual((ActionOutcome.Blocked, "rule 7", "Not today", false),
                (blocked.Outcome, blocked.BlockedBy, blocked.BlockMessage, blocked.Succeeded));
        }
    }
}
