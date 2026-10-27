using System;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Enforcement;
using Enforcer5.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Requests;

namespace Enforcer5.Tests.Enforcement
{
    /// <summary>
    /// The enforcement queue against a simulated Telegram, with its limits and 429 storms, and
    /// simulated time. No database needed, so these run in the Docker build.
    /// </summary>
    [TestClass]
    [DoNotParallelize] // the bot is static
    public class EnforcementQueueTests
    {
        private const long Raided = -1001;
        private const long Calm = -1002;

        private static string Journal(SimulatedBot bot) => $"enforcement:{Helpers.BanHistory.Edition}:jobs";

        private static void AssertNoErrors(SimulatedBot bot)
        {
            var other = bot.Errors.Where(e => !e.Contains("Too Many Requests")).ToList();
            Assert.AreEqual(0, other.Count, "Errors reported:\n" + string.Join("\n", other));
        }

        [TestMethod]
        public async Task Ban_IsCarriedOut_AndTheCallerHearsDone()
        {
            await using var bot = await SimulatedBot.StartAsync();
            bot.Group(Raided, 1);

            var result = await bot.RunAsync(EnforcementJob.Ban(Raided, 1));

            Assert.AreEqual(EnforcementOutcome.Done, result.Outcome);
            Assert.AreEqual(MemberState.Kicked, bot.Telegram.GetMember(Raided, 1));
            Assert.AreEqual(0, bot.Hashes.All(Journal(bot)).Count, "done jobs leave the journal");
            AssertNoErrors(bot);
        }

        [TestMethod]
        public async Task Kick_IsTwoCalls_AndLeavesThemFreeToComeBack()
        {
            await using var bot = await SimulatedBot.StartAsync();
            bot.Group(Raided, 1);

            var result = await bot.RunAsync(EnforcementJob.Kick(Raided, 1));

            Assert.AreEqual(EnforcementOutcome.Done, result.Outcome);
            Assert.AreEqual(MemberState.Left, bot.Telegram.GetMember(Raided, 1));
            CollectionAssert.AreEqual(new[] { "banChatMember", "unbanChatMember" }, bot.Telegram.Calls.Select(c => c.Method).ToArray());
            AssertNoErrors(bot);
        }

        [TestMethod]
        public async Task DuringAStorm_TheCallerHearsQueued_AndTheBanHappensWhenItEnds()
        {
            await using var bot = await SimulatedBot.StartAsync();
            bot.Group(Raided, 1);
            bot.Telegram.StartFullStorm(TimeSpan.FromMinutes(2), retryAfter: 60);

            var result = await bot.RunAsync(EnforcementJob.Ban(Raided, 1));

            Assert.AreEqual(EnforcementOutcome.Queued, result.Outcome);
            Assert.IsTrue(result.Succeeded, "queued counts as done for the caller: it will happen");
            Assert.AreEqual(MemberState.Member, bot.Telegram.GetMember(Raided, 1));
            Assert.AreEqual(1, bot.Hashes.All(Journal(bot)).Count, "kept in the journal meanwhile");

            await bot.AdvanceAsync(TimeSpan.FromMinutes(3));

            Assert.AreEqual(MemberState.Kicked, bot.Telegram.GetMember(Raided, 1));
            Assert.AreEqual(0, bot.Hashes.All(Journal(bot)).Count);
            Assert.AreEqual(3, bot.Telegram.Calls.Count(c => c.Method == "banChatMember"),
                "asked at 0, at 60s and at 120s, never in between");
            AssertNoErrors(bot);
        }

        [TestMethod]
        public async Task AnUnlockSubmittedWhileTheLockWaits_IsNotTakenForTheLock()
        {
            await using var bot = await SimulatedBot.StartAsync();
            bot.Group(Raided, 1);
            var open = new Telegram.Bot.Types.ChatPermissions { CanSendMessages = true };
            bot.Telegram.StartStorm(c => c.Method == "setChatPermissions", TimeSpan.FromSeconds(30), retryAfter: 30);

            await bot.RunAsync(EnforcementJob.SetPermissions(Raided, EnforcementExecutor.NothingAllowed));
            await bot.RunAsync(EnforcementJob.SetPermissions(Raided, open));
            Assert.AreEqual(true, EnforcementQueue.PendingPermissions(Raided)?.CanSendMessages, "what the chat will have");
            await bot.AdvanceAsync(TimeSpan.FromMinutes(1));

            Assert.AreEqual(true, bot.Telegram.ChatPermissions(Raided)?.CanSendMessages, "open: the unlock ran after the lock");
            Assert.IsNull(EnforcementQueue.PendingPermissions(Raided));
            AssertNoErrors(bot);
        }

        [TestMethod]
        public async Task AMuteAnEarlierOneCovers_IsNotAskedAgain_ButAfterAnUnmuteItIs()
        {
            await using var bot = await SimulatedBot.StartAsync();
            bot.Group(Raided, 1);
            var now = Helpers.Clock.UtcNow;
            int Restricts() => bot.Telegram.Calls.Count(c => c.Method == "restrictChatMember");

            await bot.RunAsync(EnforcementJob.Mute(Raided, 1)); // Mute On Join: for good
            Assert.AreEqual(EnforcementOutcome.Done, (await bot.RunAsync(EnforcementJob.Mute(Raided, 1, now.AddHours(24)))).Outcome);
            Assert.AreEqual(1, Restricts(), "a quarantine after Mute On Join is covered");

            await bot.RunAsync(EnforcementJob.Mute(Raided, 2, now.AddHours(24)));
            await bot.RunAsync(EnforcementJob.Mute(Raided, 2, now.AddHours(24).AddMinutes(1)));
            Assert.AreEqual(2, Restricts(), "a suspect's mute a moment after the quarantine is covered");
            await bot.RunAsync(EnforcementJob.Mute(Raided, 2));
            Assert.AreEqual(3, Restricts(), "for good is longer than a day");

            await bot.RunAsync(EnforcementJob.Unmute(Raided, 1));
            await bot.RunAsync(EnforcementJob.Mute(Raided, 1, now.AddHours(24)));
            Assert.AreEqual(MemberState.Restricted, bot.Telegram.GetMember(Raided, 1), "muted again after the unmute");
            Assert.AreEqual(5, Restricts());

            EnforcementQueue.ForgetMute(Raided, 1); // an admin lifted it by hand
            await bot.RunAsync(EnforcementJob.Mute(Raided, 1, now.AddHours(24)));
            Assert.AreEqual(6, Restricts());
            AssertNoErrors(bot);
        }

        [TestMethod]
        public async Task AKickWhoseUnbanIsRateLimited_ResumesAtTheUnban()
        {
            await using var bot = await SimulatedBot.StartAsync();
            bot.Group(Raided, 1);
            bot.Telegram.StartStorm(c => c.Method == "unbanChatMember", TimeSpan.FromSeconds(30), retryAfter: 30);

            await bot.RunAsync(EnforcementJob.Kick(Raided, 1));
            await bot.AdvanceAsync(TimeSpan.FromMinutes(1));

            Assert.AreEqual(MemberState.Left, bot.Telegram.GetMember(Raided, 1));
            Assert.AreEqual(1, bot.Telegram.Calls.Count(c => c.Method == "banChatMember"), "banned once");
            AssertNoErrors(bot);
        }

        [TestMethod]
        public async Task ACrashMidStorm_LosesNothing()
        {
            await using var bot = await SimulatedBot.StartAsync();
            var members = Enumerable.Range(1, 20).Select(i => (long)i).ToArray();
            bot.Group(Raided, members);
            bot.Telegram.StartFullStorm(TimeSpan.FromMinutes(5), retryAfter: 120);
            foreach (var m in members) bot.Submit(EnforcementJob.Ban(Raided, m, revokeMessages: true));
            await bot.SettleAsync();

            await bot.CrashAndRestartAsync();
            Assert.AreEqual(20, EnforcementQueue.Count, "all reloaded from the journal");
            await bot.AdvanceAsync(TimeSpan.FromMinutes(10));

            Assert.IsTrue(members.All(m => bot.Telegram.GetMember(Raided, m) == MemberState.Kicked));
            var bans = bot.Telegram.Accepted.Where(c => c.Method == "banChatMember").ToList();
            Assert.AreEqual(20, bans.Count, "each exactly once");
            Assert.IsTrue(bans.All(c => ((BanChatMemberRequest)c.Request).RevokeMessages), "the journal kept every detail");
            AssertNoErrors(bot);
        }

        [TestMethod]
        public async Task ARaidedGroup_DoesNotStarveACalmOne()
        {
            await using var bot = await SimulatedBot.StartAsync();
            var raiders = Enumerable.Range(1, 300).Select(i => (long)i).ToArray();
            bot.Group(Raided, raiders);
            bot.Group(Calm, 9999);
            foreach (var r in raiders) bot.Submit(EnforcementJob.Ban(Raided, r));
            await bot.SettleAsync();
            var submitted = bot.Clock.UtcNow;

            bot.Submit(EnforcementJob.Ban(Calm, 9999));
            await bot.AdvanceAsync(TimeSpan.FromMinutes(1));

            var calm = bot.Telegram.Accepted.Single(c => c.ChatId == Calm);
            Assert.IsTrue(calm.At - submitted <= TimeSpan.FromSeconds(1), $"the calm group waited {calm.At - submitted}");
            Assert.IsTrue(raiders.All(r => bot.Telegram.GetMember(Raided, r) == MemberState.Kicked),
                $"and the raid was dealt with: {raiders.Count(r => bot.Telegram.GetMember(Raided, r) == MemberState.Kicked)} of 300, " +
                $"{EnforcementQueue.Count} queued, calls {string.Join(",", bot.Telegram.Calls.GroupBy(c => (c.Method, c.Refused)).Select(g => $"{g.Key}={g.Count()}"))}, " +
                $"last call at {(bot.Telegram.Calls.Max(c => c.At) - submitted).TotalSeconds}s");
            Assert.AreEqual(0, bot.Telegram.LimitRefusals.Count, "under Telegram's moderation limit throughout");
            AssertNoErrors(bot);
        }

        [TestMethod]
        public async Task WithinAChat_BansGoBeforeDeletes_ButAPersonsJobsKeepTheirOrder()
        {
            await using var bot = await SimulatedBot.StartAsync();
            bot.Group(Raided, 1, 2);
            bot.Telegram.StartFullStorm(TimeSpan.FromSeconds(10), retryAfter: 10); // let them pile up

            bot.Submit(EnforcementJob.Delete(Raided, 500));
            bot.Submit(EnforcementJob.Ban(Raided, 1));
            bot.Submit(EnforcementJob.Unban(Raided, 1, BanEnd.Unbanned));
            bot.Submit(EnforcementJob.Ban(Raided, 2));
            await bot.AdvanceAsync(TimeSpan.FromMinutes(1));

            var order = bot.Telegram.Accepted.Select(c => $"{c.Method}:{c.UserId}").ToArray();
            CollectionAssert.AreEqual(new[] { "banChatMember:1", "banChatMember:2", "deleteMessage:", "unbanChatMember:1" }, order);
            Assert.AreEqual(MemberState.Left, bot.Telegram.GetMember(Raided, 1), "unbanned last, as asked");
            AssertNoErrors(bot);
        }

        [TestMethod]
        public async Task TheSameJobTwice_IsOneCall()
        {
            await using var bot = await SimulatedBot.StartAsync();
            bot.Group(Raided, 1);
            bot.Telegram.StartFullStorm(TimeSpan.FromSeconds(5), retryAfter: 5);

            bot.Submit(EnforcementJob.Ban(Raided, 1));
            bot.Submit(EnforcementJob.Ban(Raided, 1));
            await bot.AdvanceAsync(TimeSpan.FromSeconds(30));

            Assert.AreEqual(1, bot.Telegram.Accepted.Count(c => c.Method == "banChatMember"));
            AssertNoErrors(bot);
        }

        [TestMethod]
        public async Task ALateTempban_StillEndsWhenItWasMeantTo()
        {
            await using var bot = await SimulatedBot.StartAsync();
            bot.Group(Raided, 1);
            var until = bot.Clock.UtcNow.AddMinutes(30);
            bot.Telegram.StartFullStorm(TimeSpan.FromMinutes(5), retryAfter: 300);

            bot.Submit(EnforcementJob.Ban(Raided, 1, until));
            await bot.AdvanceAsync(TimeSpan.FromMinutes(6));

            var ban = (BanChatMemberRequest)bot.Telegram.Accepted.Single(c => c.Method == "banChatMember").Request;
            Assert.AreEqual(until, ban.UntilDate);
            AssertNoErrors(bot);
        }

        [TestMethod]
        public async Task BotWithoutRights_TheCallerHearsAtOnce_AndTheJobWaitsForTheRights()
        {
            await using var bot = await SimulatedBot.StartAsync();
            bot.Group(Raided, 1, 2);
            bot.Telegram.SetBotCanRestrict(Raided, false);

            var first = await bot.RunAsync(EnforcementJob.Ban(Raided, 1));
            Assert.AreEqual((EnforcementOutcome.Refused, Refusal.BotNotAdmin), (first.Outcome, first.Refusal));

            await bot.AdvanceAsync(TimeSpan.FromMinutes(3));
            bot.Telegram.SetBotCanRestrict(Raided, true);
            await bot.AdvanceAsync(TimeSpan.FromMinutes(2));
            Assert.AreEqual(MemberState.Kicked, bot.Telegram.GetMember(Raided, 1), "carried out once an admin fixed the rights");

            bot.Telegram.SetBotCanRestrict(Raided, false);
            await bot.RunAsync(EnforcementJob.Ban(Raided, 2));
            await bot.AdvanceAsync(TimeSpan.FromMinutes(15));
            Assert.AreEqual(0, EnforcementQueue.Count, "given up after ten minutes");
            Assert.IsTrue(bot.Telegram.Calls.Count(c => c.UserId == 2) <= 12, "about once a minute, no more");
            AssertNoErrors(bot);
        }

        [TestMethod]
        public async Task BanningAnAdmin_IsRefusedOnce_AndNotRetried()
        {
            await using var bot = await SimulatedBot.StartAsync();
            bot.Group(Raided);
            bot.Telegram.SetMember(Raided, 7, MemberState.Administrator);

            var result = await bot.RunAsync(EnforcementJob.Ban(Raided, 7));
            await bot.AdvanceAsync(TimeSpan.FromMinutes(5));

            Assert.AreEqual(Refusal.TargetIsAdmin, result.Refusal);
            Assert.AreEqual(1, bot.Telegram.Calls.Count);
            AssertNoErrors(bot);
        }

        [TestMethod]
        public async Task AKnownAdmin_IsNotAskedAboutAgain()
        {
            await using var bot = await SimulatedBot.StartAsync();
            bot.Group(Raided);
            bot.Telegram.SetMember(Raided, 7, MemberState.Administrator);

            await bot.RunAsync(EnforcementJob.Ban(Raided, 7));
            var again = await bot.RunAsync(EnforcementJob.Mute(Raided, 7));
            var kick = await bot.RunAsync(EnforcementJob.Kick(Raided, 7));

            Assert.AreEqual(Refusal.TargetIsAdmin, again.Refusal);
            Assert.AreEqual(Refusal.TargetIsAdmin, kick.Refusal);
            Assert.AreEqual(1, bot.Telegram.Calls.Count, "only the first reached Telegram");

            await bot.AdvanceAsync(TimeSpan.FromMinutes(11));
            bot.Telegram.SetMember(Raided, 7, MemberState.Member); // demoted meanwhile
            Assert.AreEqual(EnforcementOutcome.Done, (await bot.RunAsync(EnforcementJob.Ban(Raided, 7))).Outcome, "believed for ten minutes only");
            AssertNoErrors(bot);
        }

        [TestMethod]
        public async Task Deletes_InOneChat_GoOutAHundredAtATime()
        {
            await using var bot = await SimulatedBot.StartAsync();
            bot.Group(Raided, 1);
            for (var i = 0; i < 150; i++) bot.Telegram.Posted(Raided, 1000 + i, 1);
            bot.Telegram.StartFullStorm(TimeSpan.FromSeconds(10), retryAfter: 10); // let them pile up

            for (var i = 0; i < 150; i++) bot.Submit(EnforcementJob.Delete(Raided, 1000 + i));
            await bot.AdvanceAsync(TimeSpan.FromMinutes(1));

            Assert.AreEqual(0, bot.Telegram.MessagesFrom(Raided, 1).Count, "all gone");
            Assert.AreEqual(2, bot.Telegram.Accepted.Count(c => c.Method == "deleteMessages"), "100, then the other 50");
            Assert.AreEqual(0, EnforcementQueue.Count);
            Assert.AreEqual(0, bot.Hashes.All(Journal(bot)).Count);
            AssertNoErrors(bot);
        }

        [TestMethod]
        public async Task WithoutRights_EveryCallerHearsAtOnce_ButTelegramIsAskedAboutOnceAMinute()
        {
            await using var bot = await SimulatedBot.StartAsync();
            var members = Enumerable.Range(1, 20).Select(i => (long)i).ToArray();
            bot.Group(Raided, members);
            bot.Telegram.SetBotCanRestrict(Raided, false);

            var results = new System.Collections.Generic.List<EnforcementResult>();
            foreach (var m in members) results.Add(await bot.RunAsync(EnforcementJob.Ban(Raided, m)));
            await bot.AdvanceAsync(TimeSpan.FromMinutes(3));

            Assert.IsTrue(results.All(r => r.Refusal == Refusal.BotNotAdmin));
            Assert.IsTrue(bot.Telegram.Calls.Count <= 5, $"{bot.Telegram.Calls.Count} calls");

            bot.Telegram.SetBotCanRestrict(Raided, true);
            await bot.AdvanceAsync(TimeSpan.FromMinutes(3));
            Assert.IsTrue(members.All(m => bot.Telegram.GetMember(Raided, m) == MemberState.Kicked), "all carried out once the rights were back");
            AssertNoErrors(bot);
        }
    }
}
