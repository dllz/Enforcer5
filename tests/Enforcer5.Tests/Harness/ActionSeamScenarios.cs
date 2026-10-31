using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Actions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// The action seam, step A (<c>docs/action-seam.md</c>): every way the bot acts on a member asks for it
    /// through <see cref="ActionSeam"/>, saying where it came from. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class ActionSeamScenarios
    {
        /// <summary>Records every action the seam carries out while it is open.</summary>
        private sealed class Recorder : IDisposable
        {
            private readonly ConcurrentQueue<ActionResult> _results = new();

            internal Recorder() => ActionSeam.Ran += Add;

            private void Add(ActionResult result) => _results.Enqueue(result);

            internal ActionRequest[] Requests => _results.Select(r => r.Applied).ToArray();

            internal ActionRequest Single(ActionKind kind) => Requests.Single(r => r.Kind == kind);

            public void Dispose() => ActionSeam.Ran -= Add;
        }

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        [TestMethod]
        public async Task AdminCommands_AreCommands_ByTheAdmin()
        {
            await using var chat = await HarnessChat.StartAsync();
            using var seam = new Recorder();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "rude");

            await chat.SendAsync(admin, "/mute being rude", replyTo: message);
            await chat.SendAsync(admin, "/unmute", replyTo: message);
            await chat.SendAsync(admin, "/ban spam", replyTo: message);
            await chat.SendAsync(admin, $"/unban {member.Id}");

            CollectionAssert.AreEqual(new[] { ActionKind.Mute, ActionKind.Unmute, ActionKind.Ban, ActionKind.Unban },
                seam.Requests.Select(r => r.Kind).ToArray());
            Assert.IsTrue(seam.Requests.All(r => r.Source == ActionSource.Command && r.ActorId == admin.Id && r.TargetId == member.Id
                                                 && r.ChatId == chat.ChatId));
            Assert.AreEqual(("being rude", "spam"), (seam.Single(ActionKind.Mute).Reason, seam.Single(ActionKind.Ban).Reason));
            Assert.AreEqual(MemberState.Left, chat.Telegram.GetMember(chat.ChatId, member.Id));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ATempban_CarriesItsEnd_AndItsExpiryIsTheBots()
        {
            await using var chat = await HarnessChat.StartAsync();
            using var seam = new Recorder();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "spam");

            await chat.SendAsync(admin, "/tempban 2 h", replyTo: message);
            var tempban = seam.Single(ActionKind.TempBan);
            Assert.AreEqual((ActionSource.Command, (long?)admin.Id), (tempban.Source, tempban.ActorId));
            Assert.IsNotNull(tempban.UntilUtc);

            await chat.ExpireTempbansAsync();

            var unban = seam.Single(ActionKind.Unban);
            Assert.AreEqual((ActionSource.Expiry, (long?)null), (unban.Source, unban.ActorId));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheReportBanButton_IsAReport()
        {
            await using var chat = await HarnessChat.StartAsync();
            using var seam = new Recorder();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var reporter = chat.Add("Reporter");
            var spammer = chat.Add("Spammer");
            var spam = await chat.SendAsync(spammer, "buy now");
            await chat.SendAsync(reporter, "@admin spam", replyTo: spam);

            await chat.PressAsync(admin, chat.Telegram.LastMessageTo(admin.Id), "banflag:");

            var ban = seam.Single(ActionKind.Ban);
            Assert.AreEqual((ActionSource.Report, (long?)admin.Id, spammer.Id), (ban.Source, ban.ActorId, ban.TargetId));
            Assert.IsNotNull(ban.Report);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task MaxWarns_IsAnEscalation_WithoutAnActor()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:warnsettings", "max"] = "1";
            chat.Hashes[$"chat:{chat.ChatId}:warnsettings", "type"] = "ban";
            using var seam = new Recorder();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "rude");

            await chat.SendAsync(admin, "/warn", replyTo: message);

            var ban = seam.Single(ActionKind.Ban);
            Assert.AreEqual((ActionSource.Escalation, (long?)null, "Max warns"), (ban.Source, ban.ActorId, ban.Reason));
            Assert.IsNotNull(ban.Trigger);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AFloodBan_IsFlood()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:settings", "Flood"] = "no"; // "yes" switches it off
            chat.Hashes[$"chat:{chat.ChatId}:flood", "MaxFlood"] = "5";
            chat.Hashes[$"chat:{chat.ChatId}:flood", "ActionFlood"] = "ban";
            using var seam = new Recorder();
            var flooder = chat.Add("Flooder");

            for (var i = 0; i < 8; i++) chat.Deliver(chat.Text(flooder, $"spam {i}"));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            var ban = seam.Requests.First(r => r.Kind == ActionKind.Ban);
            Assert.AreEqual((ActionSource.Flood, (long?)null, true), (ban.Source, ban.ActorId, ban.RevokeMessages));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task MuteOnJoin_IsMuteOnJoin()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:settings", "MuteOnJoin"] = "no"; // "no" switches it on
            using var seam = new Recorder();
            var joiner = chat.Add("Joiner", MemberState.Left);

            await chat.JoinAsync(joiner);

            var mute = seam.Single(ActionKind.Mute);
            Assert.AreEqual((ActionSource.MuteOnJoin, (long?)null, joiner.Id), (mute.Source, mute.ActorId, mute.TargetId));
            Assert.AreEqual(MemberState.Restricted, chat.Telegram.GetMember(chat.ChatId, joiner.Id));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task KickMe_IsNotRecorded()
        {
            await using var chat = await HarnessChat.StartAsync();
            using var seam = new Recorder();
            var member = chat.Add("Member");

            await chat.SendAsync(member, "/kickme");

            var kick = seam.Single(ActionKind.Kick);
            Assert.AreEqual((false, (long?)null, ActionSource.Auto), (kick.Record, kick.ActorId, kick.Source));
            AssertNoErrors(chat);
        }
    }
}
