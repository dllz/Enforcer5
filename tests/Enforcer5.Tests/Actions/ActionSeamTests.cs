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
                         ActionSource.Flood, ActionSource.Limits, ActionSource.GlobalBan, ActionSource.MuteOnJoin, ActionSource.Expiry })
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
            };
            Assert.AreEqual(Enum.GetValues<ActionKind>().Length, cases.Length, "every kind is covered");
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
    }
}
