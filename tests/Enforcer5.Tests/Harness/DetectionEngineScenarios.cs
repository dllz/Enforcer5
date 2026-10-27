using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Detection step 3: the engine, and what it learns from. Signs are recorded with the member's
    /// state and what was (or would have been) done; every decision about a member, the bot's or an
    /// admin's, by command, button or in the Telegram app, is a label linked to them. Test signs stand
    /// in for the checks, which move behind the engine in step 4. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Detection")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class DetectionEngineScenarios
    {
        private static readonly SignDefinition Ban = SignCatalog.AddForTests("test_ban", 1, "ban");
        private static readonly SignDefinition Two = SignCatalog.AddForTests("test_two", 2, "score");
        private static readonly SignDefinition Other = SignCatalog.AddForTests("test_other", 2, "score");
        private static readonly SignDefinition Everyone = SignCatalog.AddForTests("test_everyone", 1, "delete", newcomersOnly: false,
            delete: true, liveFromDayOne: true);

        private sealed class DetectionRow
        {
            public string Sign { get; set; }
            public string Mode { get; set; }
            public string Action { get; set; }
            public bool Deleted { get; set; }
            public bool Reported { get; set; }
            public int ScoreBefore { get; set; }
            public int ScoreAfter { get; set; }
            public string MessageType { get; set; }
            public string TextHash { get; set; }
            public string TrustLevel { get; set; }
            public long? MemberMessages { get; set; }
            public string Outcome { get; set; }
            public long? LabelId { get; set; }
        }

        private sealed class LabelRow
        {
            public long LabelId { get; set; }
            public string Label { get; set; }
            public string Action { get; set; }
            public string Source { get; set; }
            public long? DecidedBy { get; set; }
            public string MessageType { get; set; }
            public string TextHash { get; set; }
            public string Reason { get; set; }
            public bool? Missed { get; set; }
            public bool Undone { get; set; }
        }

        private static async Task<DetectionRow[]> Detections(HarnessChat chat)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<DetectionRow>(@"
SELECT sign AS Sign, mode AS Mode, action AS Action, deleted AS Deleted, reported AS Reported, score_before AS ScoreBefore,
       score_after AS ScoreAfter, message_type AS MessageType, text_hash AS TextHash, trust_level AS TrustLevel,
       member_messages AS MemberMessages, outcome AS Outcome, label_id AS LabelId
FROM detections ORDER BY detection_id")).ToArray();
        }

        private static async Task<LabelRow[]> Labels(HarnessChat chat, long userId)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<LabelRow>(@"
SELECT l.label_id AS LabelId, l.label AS Label, l.action AS Action, l.source AS Source, actor.telegram_user_id AS DecidedBy,
       l.message_type AS MessageType, l.text_hash AS TextHash, l.reason AS Reason, l.missed AS Missed,
       l.undone_at IS NOT NULL AS Undone
FROM member_labels l
JOIN telegram_accounts a ON a.account_id = l.account_id
LEFT JOIN telegram_accounts actor ON actor.account_id = l.decided_by_account_id
WHERE a.telegram_user_id = @UserId ORDER BY l.label_id", new { UserId = userId })).ToArray();
        }

        private static async Task<int> Count(HarnessChat chat, string sql)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return await connection.ExecuteScalarAsync<int>(sql);
        }

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static MemberState State(HarnessChat chat, HarnessUser user) => chat.Telegram.GetMember(chat.ChatId, user.Id);

        private static Task GoLive(HarnessChat chat) =>
            chat.ChangeSettingsAsync(p => p with { Engine = p.Engine with { ObserveUntil = chat.Clock.GetUtcNow().AddMinutes(-1) } });

        private static async Task<DetectionOutcome> Hit(HarnessChat chat, HarnessUser user, Message message, SignDefinition sign)
        {
            var outcome = await DetectionEngine.HitAsync(chat.ChatId, user.Id, message, new SignHit(sign.Name));
            await chat.SettleAsync();
            return outcome;
        }

        [TestMethod]
        public async Task ANewGroup_Observes_RecordsWhatWouldHaveBeenDone_AndDoesNothing()
        {
            await using var chat = await HarnessChat.StartAsync();
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "buy now");

            var outcome = await Hit(chat, member, message, Ban);

            Assert.AreEqual("observe", outcome.Mode);
            Assert.AreEqual(MemberState.Member, State(chat, member), "not banned while observing");
            var stored = await new Enforcer5.Data.PostgresDetectionSettingsRepository(() => chat.Database).LoadAsync(chat.ChatId);
            var observeFor = stored.Engine.ObserveUntil.Value - chat.Clock.GetUtcNow();
            Assert.IsTrue(observeFor > TimeSpan.FromDays(13.9) && observeFor <= TimeSpan.FromDays(14), observeFor.ToString());

            var row = (await Detections(chat)).Single();
            Assert.AreEqual(("test_ban", "observe", "ban"), (row.Sign, row.Mode, row.Action));
            Assert.AreEqual("text", row.MessageType);
            Assert.IsNotNull(row.TextHash, "a hash of the text, never the text");
            Assert.AreEqual("new", row.TrustLevel);
            Assert.AreEqual(1, row.MemberMessages);
            Assert.IsNull(row.Outcome);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ALiveGroup_Acts_AndTheBotsOwnBanIsALabelThatClosesTheDetection()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "buy now");

            var outcome = await Hit(chat, member, message, Ban);

            Assert.AreEqual("live", outcome.Mode);
            Assert.AreEqual(MemberState.Kicked, State(chat, member));
            var row = (await Detections(chat)).Single();
            Assert.AreEqual(("live", "ban"), (row.Mode, row.Outcome));
            var label = (await Labels(chat, member.Id)).Single();
            Assert.AreEqual(("bad", "ban", "auto", "detect:test_ban"), (label.Label, label.Action, label.Source, label.Reason));
            Assert.AreEqual(label.LabelId, row.LabelId);
            Assert.IsNull(label.Missed, "only an admin's decision can be missed");
            Assert.AreEqual(1, await Count(chat, "SELECT count(*) FROM sanctions WHERE reason = 'detect:test_ban'"));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheScore_CountsEachSignOncePerDay_AndCrossingTheThresholdsReportsAndMutes()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            await chat.ChangeSettingsAsync(p => p with { Engine = p.Engine with { MuteEnabled = true, MuteAt = 4 } });
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "hello");

            var first = await Hit(chat, member, message, Two);
            var again = await Hit(chat, member, message, Two);
            var other = await Hit(chat, member, message, Other);

            Assert.AreEqual((0, 2, false), (first.ScoreBefore, first.ScoreAfter, first.Reported));
            Assert.AreEqual((2, 2), (again.ScoreBefore, again.ScoreAfter), "once a day");
            Assert.AreEqual((2, 4, true, "mute"), (other.ScoreBefore, other.ScoreAfter, other.Reported, other.Action),
                "past the report threshold (3) and the mute threshold (4)");
            Assert.AreEqual(MemberState.Restricted, State(chat, member));

            await chat.AdvanceAsync(TimeSpan.FromHours(25));
            var tomorrow = await Hit(chat, member, message, Two);
            Assert.AreEqual((0, 2), (tomorrow.ScoreBefore, tomorrow.ScoreAfter), "the score lasts a day");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AdminsApprovedAndEstablishedMembers_AreLeftOut_ExceptByEveryoneSigns()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var watched = chat.Add("Watched");
            chat.Keys.AddToSet($"chat:{chat.ChatId}:watch", watched.Id.ToString());
            var regular = chat.Add("Regular");
            chat.Hashes[$"{chat.ChatId}:users:{regular.Id}", "msgs"] = "5000";

            Assert.IsNull(await Hit(chat, admin, await chat.SendAsync(admin, "hi"), Everyone));
            Assert.IsNull(await Hit(chat, watched, await chat.SendAsync(watched, "hi"), Everyone));
            var regularMessage = await chat.SendAsync(regular, "hi");
            Assert.IsNull(await Hit(chat, regular, regularMessage, Two), "a newcomer sign");
            var everyone = await Hit(chat, regular, regularMessage, Everyone);
            Assert.AreEqual("live", everyone.Mode, "live from day one, even while observing");
            Assert.AreEqual(0, chat.Telegram.MessagesFrom(chat.ChatId, regular.Id).Count, "deleted");
            Assert.AreEqual("established", (await Detections(chat)).Single().TrustLevel);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnAdminsBan_AfterASign_IsTheSignsOutcome_AndSaysWhatTheMessageWas()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var spam = await chat.SendAsync(member, "cheap followers here");
            await Hit(chat, member, spam, Two); // observing: recorded, nothing done

            await chat.SendAsync(admin, "/ban", replyTo: spam);

            Assert.AreEqual(MemberState.Kicked, State(chat, member));
            var label = (await Labels(chat, member.Id)).Single(l => l.Action == "ban");
            Assert.AreEqual(("bad", "command", admin.Id), (label.Label, label.Source, label.DecidedBy));
            Assert.AreEqual("text", label.MessageType);
            Assert.AreEqual((await Detections(chat)).Single().TextHash, label.TextHash, "the same message");
            Assert.AreEqual(false, label.Missed, "a sign saw it coming");
            var row = (await Detections(chat)).Single();
            Assert.AreEqual(("ban", label.LabelId), (row.Outcome, row.LabelId));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ADecisionWrittenBeforeItsDetection_IsLinkedWhenTheDetectionIsWritten()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var spam = await chat.SendAsync(member, "cheap followers here");
            // The detection waits for the minute's flush; the ban is written at once.
            await DetectionEngine.HitAsync(chat.ChatId, member.Id, spam, new SignHit(Two.Name));
            await chat.SendAsync(admin, "/ban", replyTo: spam);

            var row = (await Detections(chat)).Single();
            Assert.AreEqual("ban", row.Outcome);
            Assert.AreEqual(false, (await Labels(chat, member.Id)).Single(l => l.Action == "ban").Missed);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnAdminsDecision_NoSignSawComing_IsMissed()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "something only this group minds");

            await chat.SendAsync(admin, "/warn", replyTo: message);
            await chat.SendAsync(admin, "/delmsg", replyTo: message);

            var labels = await Labels(chat, member.Id);
            Assert.AreEqual(2, labels.Length, string.Join(", ", labels.Select(l => l.Action)));
            Assert.IsTrue(labels.All(l => l.Missed == true && l.Source == "command"));
            CollectionAssert.AreEquivalent(new[] { "warn", "delete" }, labels.Select(l => l.Action).ToArray());
            Assert.IsTrue(labels.All(l => l.MessageType == "text" && l.TextHash != null));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnUnban_TakesTheBanBack_AndItsDetections()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "hello");
            await Hit(chat, member, message, Two);
            await chat.SendAsync(admin, "/ban", replyTo: message);

            await chat.SendAsync(admin, $"/unban {member.Id}");

            var labels = await Labels(chat, member.Id);
            Assert.IsTrue(labels.Single(l => l.Action == "ban").Undone);
            Assert.AreEqual(("undo", "command"), (labels.Single(l => l.Action == "unban").Label, labels.Single(l => l.Action == "unban").Source));
            Assert.AreEqual("undone", (await Detections(chat)).Single().Outcome);
            AssertNoErrors(chat);
        }

        private static Update AppChange(HarnessChat chat, HarnessUser by, HarnessUser member, ChatMember before, ChatMember after) =>
            new Update
            {
                Id = 900000 + chat.Telegram.NextMessageId(),
                ChatMember = new ChatMemberUpdated
                {
                    Chat = new Chat { Id = chat.ChatId, Type = ChatType.Supergroup, Title = chat.Title },
                    From = by.ToUser(),
                    Date = chat.Clock.UtcNow,
                    OldChatMember = before,
                    NewChatMember = after
                }
            };

        [TestMethod]
        public async Task ABanMadeInTheTelegramApp_IsInTheHistoryAndALabel_OnceEvenIfBothEditionsSeeIt()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            await chat.SendAsync(member, "hello");
            var ban = AppChange(chat, admin, member, new ChatMemberMember { User = member.ToUser() },
                new ChatMemberBanned { User = member.ToUser() });

            chat.Deliver(ban);
            // The other edition's copy, as far as the database goes.
            chat.Deliver(new Update { Id = ban.Id + 1, ChatMember = ban.ChatMember });
            await chat.SettleAsync();

            var label = (await Labels(chat, member.Id)).Single();
            Assert.AreEqual(("bad", "ban", "app", admin.Id, true), (label.Label, label.Action, label.Source, label.DecidedBy, label.Missed));
            Assert.AreEqual(1, await Count(chat, "SELECT count(*) FROM sanctions WHERE reason = 'In the Telegram app' AND kind = 'ban'"));

            chat.Deliver(AppChange(chat, admin, member, new ChatMemberBanned { User = member.ToUser() },
                new ChatMemberLeft { User = member.ToUser() }));
            await chat.SettleAsync();
            Assert.IsTrue((await Labels(chat, member.Id)).Single(l => l.Action == "ban").Undone);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheBotsOwnChangesAndMembersLeaving_AreNotTheAdminsDecisions()
        {
            await using var chat = await HarnessChat.StartAsync();
            var member = chat.Add("Member");
            var bot = new HarnessUser(HarnessChat.BotUser.Id, "Enforcer", IsBot: true);
            chat.Deliver(AppChange(chat, member, member, new ChatMemberMember { User = member.ToUser() },
                new ChatMemberLeft { User = member.ToUser() }));
            chat.Deliver(AppChange(chat, bot, member, new ChatMemberMember { User = member.ToUser() },
                new ChatMemberBanned { User = member.ToUser() }));
            await chat.SettleAsync();

            Assert.AreEqual(0, await Count(chat, "SELECT count(*) FROM member_labels"));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AfterARestart_AMembersScoreIsRebuiltFromTheDetections()
        {
            await using var chat = await HarnessChat.StartAsync();
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "hello");
            await Hit(chat, member, message, Two);

            await chat.CrashAndRestartAsync();
            var next = await Hit(chat, member, message, Other);

            Assert.AreEqual((2, 4), (next.ScoreBefore, next.ScoreAfter));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task MuteOnJoin_IsNotADecisionAboutTheMember()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:settings", "MuteOnJoin"] = "no";
            var joiner = chat.Add("Joiner", MemberState.Left);
            await chat.JoinAsync(joiner);

            Assert.AreEqual(MemberState.Restricted, State(chat, joiner));
            Assert.AreEqual(0, await Count(chat, "SELECT count(*) FROM member_labels"));
            AssertNoErrors(chat);
        }
    }
}
