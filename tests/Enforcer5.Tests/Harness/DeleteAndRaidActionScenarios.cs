using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Actions;
using Enforcer5.Raids;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// The action seam, step D (<c>docs/action-seam.md</c>): deletes an admin asks for are actions, and record
    /// a decision about the author (now also /d); raid mode's own actions go through the seam too, labelled as
    /// before. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class DeleteAndRaidActionScenarios
    {
        private sealed class Recorder : IDisposable
        {
            private readonly ConcurrentQueue<ActionResult> _results = new();
            internal Recorder() => ActionSeam.Ran += Add;
            private void Add(ActionResult result) => _results.Enqueue(result);
            internal ActionResult[] Results => _results.ToArray();
            internal ActionRequest[] Of(ActionKind kind) => Results.Select(r => r.Applied).Where(r => r.Kind == kind).ToArray();
            public void Dispose() => ActionSeam.Ran -= Add;
        }

        private sealed class LabelRow
        {
            public string Label { get; set; }
            public string Action { get; set; }
            public string Source { get; set; }
            public long? DecidedBy { get; set; }
        }

        private static async Task<LabelRow[]> Labels(HarnessChat chat, long userId)
        {
            await chat.SettleAsync();
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<LabelRow>(@"
SELECT l.label AS Label, l.action AS Action, l.source AS Source, actor.telegram_user_id AS DecidedBy
FROM member_labels l
JOIN telegram_accounts a ON a.account_id = l.account_id
LEFT JOIN telegram_accounts actor ON actor.account_id = l.decided_by_account_id
WHERE a.telegram_user_id = @UserId ORDER BY l.label_id", new { UserId = userId })).ToArray();
        }

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        [TestMethod]
        public async Task SlashDelmsg_IsADelete_ByTheAdmin_AboutTheAuthor()
        {
            await using var chat = await HarnessChat.StartAsync();
            using var seam = new Recorder();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "buy followers");

            await chat.SendAsync(admin, "/delmsg", replyTo: post);

            var delete = seam.Of(ActionKind.Delete).Single();
            Assert.AreEqual((ActionSource.Command, (long?)admin.Id, member.Id, post.MessageId),
                (delete.Source, delete.ActorId, delete.TargetId, delete.MessageId));
            Assert.IsFalse(chat.Telegram.MessagesFrom(chat.ChatId, member.Id).Contains(post.MessageId));
            var label = (await Labels(chat, member.Id)).Single();
            Assert.AreEqual(("bad", "delete", "command", (long?)admin.Id), (label.Label, label.Action, label.Source, label.DecidedBy));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task SlashD_ByAnAdmin_RecordsADecision_AndDeletesTheCommand()
        {
            await using var chat = await HarnessChat.StartAsync();
            using var seam = new Recorder();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "buy followers");

            var command = await chat.SendAsync(admin, "/d", replyTo: post);

            Assert.AreEqual(0, chat.Telegram.MessagesFrom(chat.ChatId, member.Id).Count, "the post is gone");
            Assert.IsFalse(chat.Telegram.MessagesFrom(chat.ChatId, admin.Id).Contains(command.MessageId), "and the /d too");
            var delete = seam.Of(ActionKind.Delete).Single();
            Assert.AreEqual((ActionSource.Command, (long?)admin.Id, member.Id), (delete.Source, delete.ActorId, delete.TargetId));
            var label = (await Labels(chat, member.Id)).Single();
            Assert.AreEqual(("bad", "delete", "command", (long?)admin.Id), (label.Label, label.Action, label.Source, label.DecidedBy));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task SlashD_OnOnesOwnPost_IsNoDecision()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "oops, wrong chat");

            await chat.SendAsync(member, "/d", replyTo: post);

            Assert.AreEqual(0, chat.Telegram.MessagesFrom(chat.ChatId, member.Id).Count, "the post and the /d are gone");
            Assert.AreEqual(0, (await Labels(chat, member.Id)).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task SlashD_OnSomeoneElsesPost_ByAMember_DoesNothing()
        {
            await using var chat = await HarnessChat.StartAsync();
            using var seam = new Recorder();
            chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var other = chat.Add("Other");
            var post = await chat.SendAsync(other, "hello");

            await chat.SendAsync(member, "/d", replyTo: post);

            Assert.IsTrue(chat.Telegram.MessagesFrom(chat.ChatId, other.Id).Contains(post.MessageId));
            Assert.AreEqual(0, seam.Of(ActionKind.Delete).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheReportDeleteButton_IsAReport()
        {
            await using var chat = await HarnessChat.StartAsync();
            using var seam = new Recorder();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var reporter = chat.Add("Reporter");
            var spammer = chat.Add("Spammer");
            var spam = await chat.SendAsync(spammer, "buy now");
            await chat.SendAsync(reporter, "@admin spam", replyTo: spam);

            await chat.PressAsync(admin, chat.Telegram.LastMessageTo(admin.Id), "delflag:");

            var delete = seam.Of(ActionKind.Delete).Single();
            Assert.AreEqual((ActionSource.Report, (long?)admin.Id, spammer.Id, spam.MessageId),
                (delete.Source, delete.ActorId, delete.TargetId, delete.MessageId));
            Assert.IsFalse(chat.Telegram.MessagesFrom(chat.ChatId, spammer.Id).Contains(spam.MessageId));
            var label = (await Labels(chat, spammer.Id)).Single();
            Assert.AreEqual(("bad", "delete", "report"), (label.Label, label.Action, label.Source));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task RaidModesOwnActions_GoThroughTheSeam_AndStayLabelledAuto()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Add("Admin", MemberState.Administrator);
            RaidGuard.SetMode(chat.ChatId, RaidMode.Auto);
            for (var i = 0; i < 3; i++)
                chat.Hashes[$"{chat.ChatId}:users:{chat.Add($"Regular{i}").Id}", "msgs"] = "800";
            using var seam = new Recorder();

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

            var raid = seam.Results.Where(r => r.Applied.Source == ActionSource.Raid).ToArray();
            Assert.IsTrue(raid.Length > 0, "raid mode acted through the seam");
            Assert.IsTrue(raid.All(r => r.Applied.ActorId == null && raiders.Any(x => x.Id == r.Applied.TargetId)));
            Assert.IsTrue(raid.All(r => r.Applied.Kind is ActionKind.Ban or ActionKind.TempMute), string.Join(",", raid.Select(r => r.Applied.Kind)));
            var banned = raiders.Where(r => chat.Telegram.GetMember(chat.ChatId, r.Id) == MemberState.Kicked).ToArray();
            Assert.IsTrue(banned.Length > 0, "auto mode banned the raiders who posted the scam");
            foreach (var raider in banned)
                Assert.IsTrue((await Labels(chat, raider.Id)).All(l => l.Source == "auto" && l.DecidedBy == null),
                    "the bot's own decision, as before");
            Assert.IsTrue(chat.Errors.All(e => e.Contains("Too Many Requests")), string.Join("\n", chat.Errors));
        }
    }
}
