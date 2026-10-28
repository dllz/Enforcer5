using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Detection step 4: every automatic check that existed before the engine tells it what it did, and
    /// the engine records that in <c>detections</c>, live from day one, with the bot's own action linked
    /// as its outcome. What the checks do is unchanged: <see cref="AutoCheckScenarios"/> and the flood,
    /// media and raid scenarios pass as they were. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Detection")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class PortedCheckScenarios
    {
        private sealed class Row
        {
            public string Sign { get; set; }
            public string Mode { get; set; }
            public string Action { get; set; }
            public bool Deleted { get; set; }
            public bool Reported { get; set; }
            public int Weight { get; set; }
            public int ScoreAfter { get; set; }
            public long? UserId { get; set; }
            public bool NoAccount { get; set; }
            public string MessageType { get; set; }
            public string TrustLevel { get; set; }
            public string Evidence { get; set; }
            public string Outcome { get; set; }
        }

        private static async Task<Row[]> Detections(HarnessChat chat)
        {
            await chat.SettleAsync();
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<Row>(@"
SELECT d.sign AS Sign, d.mode AS Mode, d.action AS Action, d.deleted AS Deleted, d.reported AS Reported, d.weight AS Weight,
       d.score_after AS ScoreAfter, a.telegram_user_id AS UserId, d.account_id IS NULL AS NoAccount,
       d.message_type AS MessageType, d.trust_level AS TrustLevel, d.evidence::text AS Evidence, d.outcome AS Outcome
FROM detections d LEFT JOIN telegram_accounts a ON a.account_id = d.account_id
ORDER BY d.detection_id")).ToArray();
        }

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static MemberState State(HarnessChat chat, HarnessUser user) => chat.Telegram.GetMember(chat.ChatId, user.Id);

        private static void FloodOn(HarnessChat chat, string action)
        {
            chat.Hashes[$"chat:{chat.ChatId}:settings", "Flood"] = "no"; // "yes" switches it off
            chat.Hashes[$"chat:{chat.ChatId}:flood", "MaxFlood"] = "5";
            chat.Hashes[$"chat:{chat.ChatId}:flood", "ActionFlood"] = action;
        }

        [TestMethod]
        public async Task Flood_IsRecordedOnce_LiveWhileTheGroupObserves_AndTheKickIsItsOutcome()
        {
            await using var chat = await HarnessChat.StartAsync();
            FloodOn(chat, "kick");
            var flooder = chat.Add("Flooder");

            for (var i = 0; i < 8; i++) chat.Deliver(chat.Text(flooder, $"spam {i}"));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            Assert.AreEqual(MemberState.Left, State(chat, flooder));
            var row = (await Detections(chat)).Single();
            Assert.AreEqual(("flood", "live", "kick", true), (row.Sign, row.Mode, row.Action, row.Deleted),
                "live from day one, though a new group observes the newcomer signs");
            Assert.AreEqual((0, 0), (row.Weight, row.ScoreAfter), "the ported checks add nothing to the score");
            Assert.AreEqual((flooder.Id, "new", "text"), (row.UserId, row.TrustLevel, row.MessageType));
            StringAssert.Contains(row.Evidence, "\"limit\": 5");
            Assert.AreEqual("kick", row.Outcome, "the bot's own kick is the detection's outcome");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TextLength_IsRecordedWithTheLengths_NeverTheText()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "enabled"] = "yes";
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "maxlength"] = "100";
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "maxlines"] = "50";
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "action"] = "ban";
            var talker = chat.Add("Talker");

            await chat.SendAsync(talker, new string('q', 150));

            var row = (await Detections(chat)).Single();
            Assert.AreEqual(("text_length", "ban", true, "ban"), (row.Sign, row.Action, row.Deleted, row.Outcome));
            StringAssert.Contains(row.Evidence, "\"length\": 150");
            Assert.IsFalse(row.Evidence.Contains("qqq"), row.Evidence);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TextLength_WithNoActionChosen_IsRecordedAsNone_ButDeleted()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "enabled"] = "yes";
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "maxlength"] = "100";
            var talker = chat.Add("Talker");

            await chat.SendAsync(talker, new string('q', 150));

            var row = (await Detections(chat)).Single();
            Assert.AreEqual(("text_length", "none", true), (row.Sign, row.Action, row.Deleted));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task NameLength_IsRecorded_WithoutADelete()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:antinamelengthsettings", "enabled"] = "yes";
            chat.Hashes[$"chat:{chat.ChatId}:antinamelengthsettings", "maxlength"] = "10";
            chat.Hashes[$"chat:{chat.ChatId}:antinamelengthsettings", "action"] = "kick";
            var named = chat.Add("A name far too long");

            await chat.SendAsync(named, "hi");

            var row = (await Detections(chat)).Single();
            Assert.AreEqual(("name_length", "kick", false, "kick"), (row.Sign, row.Action, row.Deleted, row.Outcome));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task MediaLock_EachStrikeIsADelete_AndTheLastTheGroupsAction()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:media", "image"] = "blocked";
            chat.Hashes[$"chat:{chat.ChatId}:media", "action"] = "kick";
            chat.Hashes[$"chat:{chat.ChatId}:warnsettings", "mediamax"] = "2";
            var poster = chat.Add("Poster");

            await chat.SendPhotoAsync(poster, "one");
            await chat.SendPhotoAsync(poster, "two");

            var rows = await Detections(chat);
            CollectionAssert.AreEqual(new[] { "delete", "kick" }, rows.Select(r => r.Action).ToArray());
            Assert.IsTrue(rows.All(r => r.Sign == "media_lock" && r.Deleted && r.MessageType == "photo"));
            StringAssert.Contains(rows[0].Evidence, "\"strike\": 1");
            StringAssert.Contains(rows[1].Evidence, "\"type\": \"image\"");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Scripts_ArabicOnJoining_AndRightToLeft_AreRecordedAgainstTheMemberActedOn()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:char", "Arab"] = "kick";
            chat.Hashes[$"chat:{chat.ChatId}:char", "Rtl"] = "ban";
            var adder = chat.Add("Adder");
            var joiner = chat.Add("بدر", MemberState.Left);
            var join = chat.Join(joiner, addedBy: adder);
            await chat.PostAsync(join);
            var writer = chat.Add("Writer");
            await chat.SendAsync(writer, "abc\u202Edef");

            var rows = await Detections(chat);
            var arabic = rows.Single(r => r.Sign == "script_arabic");
            Assert.AreEqual((joiner.Id, "kick", (string)null), (arabic.UserId, arabic.Action, arabic.MessageType),
                "the joiner, not the adder, and the join message is not theirs");
            StringAssert.Contains(arabic.Evidence, "\"onJoin\": true");
            var rtl = rows.Single(r => r.Sign == "script_rtl");
            Assert.AreEqual((writer.Id, "ban", "ban"), (rtl.UserId, rtl.Action, rtl.Outcome));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task BlockedInlineBotsAndStickerPacks_AreRecordedAsDeletes_ButNotForAdmins()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Keys.AddToSet($"chat:{chat.ChatId}:blockedinline", "u:spambot");
            chat.Keys.AddToSet($"chat:{chat.ChatId}:blockedstickersets", "badpack");
            var member = chat.Add("Member");
            var admin = chat.Add("Admin", MemberState.Administrator);
            var spamBot = new User { Id = 909090, IsBot = true, FirstName = "Spam", Username = "SpamBot" };

            var viaBot = chat.Text(member, "via a bot");
            viaBot.ViaBot = spamBot;
            var adminViaBot = chat.Text(admin, "via a bot too");
            adminViaBot.ViaBot = spamBot;
            await chat.PostAsync(viaBot);
            await chat.PostAsync(adminViaBot);
            await chat.PostAsync(new Message
            {
                Id = chat.Telegram.NextMessageId(),
                Chat = new Chat { Id = chat.ChatId, Type = ChatType.Supergroup, Title = chat.Title },
                From = member.ToUser(),
                Date = chat.Clock.UtcNow,
                Sticker = new Sticker { FileId = "st", FileUniqueId = "stu", Type = StickerType.Regular, Width = 512, Height = 512, SetName = "BadPack" }
            });

            var rows = await Detections(chat);
            CollectionAssert.AreEqual(new[] { "inline_bot", "sticker_set" }, rows.Select(r => r.Sign).ToArray());
            Assert.IsTrue(rows.All(r => r.UserId == member.Id && r.Action == "delete" && r.Deleted));
            StringAssert.Contains(rows[0].Evidence, "909090");
            StringAssert.Contains(rows[1].Evidence, "BadPack");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AChannelPost_IsRecordedWithTheChannel_AndNoMember()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:channelposts", "enabled"] = "yes";
            const long linked = -1009999000001, other = -1009999000002;
            chat.Telegram.SetLinkedChannel(chat.ChatId, linked);
            var placeholder = new HarnessUser(136817688, "Channel");

            Message AsChannel(long channelId)
            {
                var m = chat.Text(placeholder, "promo");
                m.SenderChat = new Chat { Id = channelId, Type = ChatType.Channel, Title = "Chan" + channelId };
                return m;
            }

            await chat.PostAsync(AsChannel(other));
            await chat.PostAsync(AsChannel(linked));

            var row = (await Detections(chat)).Single();
            Assert.AreEqual(("channel_post", "ban", true, true), (row.Sign, row.Action, row.Deleted, row.NoAccount),
                "the member behind a channel cannot be seen, and the placeholder is nobody");
            Assert.IsNull(row.TrustLevel);
            StringAssert.Contains(row.Evidence, other.ToString());
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ANewcomersBareLink_IsRecordedAsAReport()
        {
            await using var chat = await HarnessChat.StartAsync();
            var regular = chat.Add("Regular");
            chat.Hashes[$"{chat.ChatId}:users:{regular.Id}", "msgs"] = "800";
            var newcomer = chat.Add("Newcomer", MemberState.Left);
            await chat.JoinAsync(newcomer);

            await chat.SendAsync(regular, "https://example.com/news");
            await chat.SendAsync(newcomer, "https://t.me/+cheapfollowers");

            var row = (await Detections(chat)).Single();
            Assert.AreEqual(("raid_link", "report", true, false, newcomer.Id), (row.Sign, row.Action, row.Reported, row.Deleted, row.UserId));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AdminsAndWatchedMembers_LeaveNoDetections()
        {
            await using var chat = await HarnessChat.StartAsync();
            FloodOn(chat, "kick");
            chat.Hashes[$"chat:{chat.ChatId}:media", "image"] = "blocked";
            chat.Hashes[$"chat:{chat.ChatId}:media", "action"] = "kick";
            chat.Hashes[$"chat:{chat.ChatId}:char", "Arab"] = "kick";
            chat.Hashes[$"chat:{chat.ChatId}:char", "Rtl"] = "kick";
            var admin = chat.Add("Admin", MemberState.Administrator);
            var watched = chat.Add("Watched");
            chat.Keys.AddToSet($"chat:{chat.ChatId}:watch", watched.Id.ToString());

            foreach (var who in new[] { admin, watched })
            {
                await chat.SendPhotoAsync(who, "pic");
                await chat.SendAsync(who, "سلام \u202E");
                for (var i = 0; i < 7; i++) chat.Deliver(chat.Text(who, $"announcement {i}"));
            }
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            Assert.AreEqual(0, (await Detections(chat)).Length);
            AssertNoErrors(chat);
        }

#if PREMIUM
        [TestMethod]
        [Timeout(120000)]
        public async Task MassAdd_RecordsEveryoneAddedAndTheAdder()
        {
            await using var chat = await HarnessChat.StartAsync(chatId: -1001060486754);
            var adder = chat.Add("Adder");
            var added = Enumerable.Range(0, 2).Select(i => chat.Add($"Added{i}", MemberState.Left)).ToList();
            var join = chat.Join(added[0], addedBy: adder);
            foreach (var a in added) chat.Telegram.SetMember(chat.ChatId, a.Id, MemberState.Member);
            join.NewChatMembers = added.Select(a => a.ToUser()).ToArray();

            await chat.PostAsync(join);

            var rows = (await Detections(chat)).Where(r => r.Sign == "mass_add").ToArray();
            CollectionAssert.AreEquivalent(added.Select(a => (long?)a.Id).Append(adder.Id).ToArray(), rows.Select(r => r.UserId).ToArray());
            Assert.IsTrue(rows.All(r => r.Action == "tempban" && r.Outcome == "tempban"), string.Join("; ", rows.Select(r => $"{r.UserId} {r.Action} {r.Outcome}")));
            AssertNoErrors(chat);
        }
#endif
    }
}
