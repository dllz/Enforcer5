using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Actions;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Enforcer5.Raids;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// The action seam, step F (<c>docs/action-seam.md</c>, sections 5 and 6): before hooks block or change what
    /// an admin chose, after hooks hear about it; a hook that throws or overruns counts as allow; the bot's
    /// defences and the creator's and devs' actions never wait on a hook. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class ActionHookScenarios
    {
        private sealed class TestHook : IActionHook, IDisposable
        {
            private readonly Func<ActionRequest, Task<HookVerdict>> _before;
            private readonly Func<ActionRequest, bool> _wants;
            internal readonly ConcurrentQueue<ActionRequest> Asked = new();
            internal readonly ConcurrentQueue<ActionResult> Heard = new();

            internal TestHook(Func<ActionRequest, Task<HookVerdict>> before, Func<ActionRequest, bool> wants = null)
            {
                _before = before;
                _wants = wants ?? (_ => true);
                ActionHooks.Add(this);
            }

            public string Name => "test rule";
            public bool Wants(ActionRequest request) => _wants(request);

            public Task<HookVerdict> BeforeAsync(ActionRequest request)
            {
                Asked.Enqueue(request);
                return _before(request);
            }

            public Task AfterAsync(ActionResult result)
            {
                Heard.Enqueue(result);
                return Task.CompletedTask;
            }

            public void Dispose() => ActionHooks.Remove(this);
        }

        private sealed class Recorder : IDisposable
        {
            internal readonly ConcurrentQueue<ActionResult> Results = new();
            internal Recorder() => ActionSeam.Ran += Add;
            private void Add(ActionResult result) => Results.Enqueue(result);
            internal ActionResult Single(ActionKind kind) => Results.Single(r => r.Applied.Kind == kind);
            public void Dispose() => ActionSeam.Ran -= Add;
        }

        private static Task<HookVerdict> Allow(ActionRequest _) => Task.FromResult(HookVerdict.Allow);

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        [TestCleanup]
        public void Cleanup() => ActionHooks.ResetForTests();

        [TestMethod]
        public async Task ABlockingHook_StopsAnAdminsBan_AndTheAdminIsToldWhy()
        {
            await using var chat = await HarnessChat.StartAsync();
            using var seam = new Recorder();
            using var hook = new TestHook(_ => Task.FromResult(HookVerdict.Block("No bans on Sundays")));
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "spam");

            await chat.SendAsync(admin, "/ban spam", replyTo: post);

            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, member.Id));
            Assert.AreEqual(0, chat.Telegram.Accepted.Count(c => c.Method == "banChatMember"), "Telegram never asked");
            var ban = seam.Single(ActionKind.Ban);
            Assert.AreEqual((ActionOutcome.Blocked, "test rule", "No bans on Sundays"), (ban.Outcome, ban.BlockedBy, ban.BlockMessage));
            Assert.IsTrue(chat.Telegram.SentMessages.Any(m => m.Chat.Id == chat.ChatId && m.Text == "No bans on Sundays"));
            await chat.SettleAsync();
            Assert.AreEqual(ActionOutcome.Blocked, hook.Heard.Single().Outcome, "after hooks hear a blocked action too");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AChangingHook_ShortensATempban_AndGivesItsReason()
        {
            await using var chat = await HarnessChat.StartAsync();
            using var seam = new Recorder();
            var shorter = Clock.UtcNow.AddMinutes(30);
            using var hook = new TestHook(r => Task.FromResult(new HookVerdict { UntilUtc = shorter, Reason = "first offence" }),
                r => r.Kind == ActionKind.TempBan);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "spam");

            await chat.SendAsync(admin, "/tempban 2 h", replyTo: post);

            var tempban = seam.Single(ActionKind.TempBan);
            Assert.AreEqual((ActionOutcome.Done, shorter, "first offence"), (tempban.Outcome, tempban.Applied.UntilUtc, tempban.Applied.Reason));
            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, member.Id));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AHookThatThrows_OrOverruns_IsAllowed_AndReported()
        {
            await using var chat = await HarnessChat.StartAsync();
            using var throwing = new TestHook(_ => throw new InvalidOperationException("rule bug"), r => r.Kind == ActionKind.Kick);
            using var slow = new TestHook(async _ =>
            {
                await Clock.Delay(TimeSpan.FromSeconds(30));
                return HookVerdict.Block("too late to matter");
            }, r => r.Kind == ActionKind.Ban);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var kicked = chat.Add("Kicked");
            var banned = chat.Add("Banned");

            await chat.SendAsync(admin, $"/kick {kicked.Id}");
            await chat.SendAsync(admin, $"/ban {banned.Id}");

            Assert.AreEqual(MemberState.Left, chat.Telegram.GetMember(chat.ChatId, kicked.Id));
            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, banned.Id));
            Assert.IsTrue(chat.Errors.Any(e => e.Contains("rule bug")), string.Join("\n", chat.Errors));
            Assert.IsTrue(chat.Errors.Any(e => e.Contains("took longer than its budget")), string.Join("\n", chat.Errors));
        }

        [TestMethod]
        public async Task ABlockedEscalation_KeepsTheWarns_AndSaysWhy()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:warnsettings", "max"] = "1";
            chat.Hashes[$"chat:{chat.ChatId}:warnsettings", "type"] = "ban";
            using var hook = new TestHook(_ => Task.FromResult(HookVerdict.Block("Mute them instead")),
                r => r.Source == ActionSource.Escalation);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "rude");

            await chat.SendAsync(admin, "/warn", replyTo: post);

            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, member.Id));
            Assert.AreEqual(1L, chat.WarnCount(member), "the warns stay");
            StringAssert.Contains(chat.Telegram.SentMessages.Last(m => m.Chat.Id == chat.ChatId).Text, "Mute them instead");
            Assert.AreEqual(ActionSource.Escalation, hook.Asked.Single().Source, "the warn itself was not asked about");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheCreatorAndTheDevs_AreNeverHeldUp()
        {
            await using var chat = await HarnessChat.StartAsync();
            using var hook = new TestHook(_ => Task.FromResult(HookVerdict.Block("no")));
            var creator = chat.Add("Creator", MemberState.Creator);
            var first = chat.Add("First");
            var second = chat.Add("Second");
            var dev = chat.Add("Dev", MemberState.Administrator, id: Constants.Devs[0]);

            await chat.SendAsync(creator, $"/ban {first.Id}");
            await chat.SendAsync(dev, $"/ban {second.Id}");

            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, first.Id));
            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, second.Id));
            Assert.AreEqual(0, hook.Asked.Count);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheBotsOwnChecks_AreNeverAsked_ButHeardAbout()
        {
            await using var chat = await HarnessChat.StartAsync();
            using var hook = new TestHook(_ => Task.FromResult(HookVerdict.Block("no")));
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "enabled"] = "yes";
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "maxlength"] = "100";
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "maxlines"] = "50";
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "action"] = "kick";
            chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");

            await chat.SendAsync(member, new string('a', 150));
            await chat.SettleAsync();

            Assert.AreEqual(MemberState.Left, chat.Telegram.GetMember(chat.ChatId, member.Id));
            Assert.AreEqual(0, hook.Asked.Count);
            var heard = hook.Heard.Single();
            Assert.AreEqual((ActionKind.Kick, ActionSource.Limits, ActionOutcome.Done), (heard.Applied.Kind, heard.Applied.Source, heard.Outcome));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ARaid_DoesNotNoticeASlowHook()
        {
            await using var chat = await HarnessChat.StartAsync();
            using var hook = new TestHook(async _ =>
            {
                await Clock.Delay(TimeSpan.FromMinutes(5));
                return HookVerdict.Block("no");
            });
            chat.Add("Admin", MemberState.Administrator);
            RaidGuard.SetMode(chat.ChatId, RaidMode.Auto);
            for (var i = 0; i < 3; i++)
                chat.Hashes[$"{chat.ChatId}:users:{chat.Add($"Regular{i}").Id}", "msgs"] = "800";

            var raiders = new List<HarnessUser>();
            for (var i = 0; i < 12; i++)
            {
                var raider = chat.Add($"Raider{i}", MemberState.Left);
                raiders.Add(raider);
                chat.Deliver(chat.Join(raider));
                await chat.AdvanceAsync(TimeSpan.FromSeconds(1));
                chat.Deliver(chat.Photo(raider, uniqueId: "scam-picture", caption: "FREE crypto t.me/scam"));
            }
            await chat.AdvanceAsync(TimeSpan.FromSeconds(35));

            Assert.IsTrue(raiders.Count(r => chat.Telegram.GetMember(chat.ChatId, r.Id) == MemberState.Kicked) > 0);
            Assert.IsTrue(hook.Asked.All(r => r.Source != ActionSource.Raid), "raid mode's own actions never wait on a hook");
            Assert.IsTrue(hook.Heard.All(r => r.Applied.Source != ActionSource.Raid), "nor are they handed to one");
        }
    }
}
