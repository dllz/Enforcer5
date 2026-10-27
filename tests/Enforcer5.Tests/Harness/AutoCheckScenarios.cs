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
    /// Pins what every existing automatic check does today, before Detection v2 moves them behind
    /// one engine (docs/detection-design.md, build step 1). These describe the behaviour as found,
    /// quirks included, so the port can show it changed nothing: a scenario here may only change
    /// together with a deliberate change of behaviour, named in that commit.
    /// Flood kick/ban/off, Mute On Join and media bans already have scenarios in FloodScenarios,
    /// KickAndMuteScenarios and WarnHistoryScenarios; this adds the rest. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Detection")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class AutoCheckScenarios
    {
        private sealed class Sanction
        {
            public string Kind { get; set; }
            public string Reason { get; set; }
            public long Target { get; set; }
        }

        private static async Task<Sanction[]> Sanctions(HarnessChat chat)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<Sanction>(@"
SELECT s.kind AS Kind, s.reason AS Reason, a.telegram_user_id AS Target
FROM sanctions s JOIN telegram_accounts a ON a.account_id = s.account_id
ORDER BY s.sanction_id")).ToArray();
        }

        private static async Task<string[]> WarnReasons(HarnessChat chat)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<string>(
                "SELECT warning_type || ' ' || operation || ' ' || coalesce(reason, '') FROM warning_events ORDER BY warning_event_id")).ToArray();
        }

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static MemberState State(HarnessChat chat, HarnessUser user) => chat.Telegram.GetMember(chat.ChatId, user.Id);

        private static int Deletes(HarnessChat chat) => chat.Telegram.Accepted.Count(c => c.Method.StartsWith("deleteMessage"));

        // ---------- Flood (the actions FloodScenarios does not cover) ----------

        private static void FloodOn(HarnessChat chat, string action, int max = 5)
        {
            chat.Hashes[$"chat:{chat.ChatId}:settings", "Flood"] = "no"; // "yes" switches it off
            chat.Hashes[$"chat:{chat.ChatId}:flood", "MaxFlood"] = max.ToString();
            chat.Hashes[$"chat:{chat.ChatId}:flood", "ActionFlood"] = action;
        }

        [TestMethod]
        public async Task Flood_Warn_WarnsOnce_KeepsTheMember_AndDeletesTheFlood()
        {
            await using var chat = await HarnessChat.StartAsync();
            FloodOn(chat, "warn");
            chat.Hashes[$"chat:{chat.ChatId}:warnsettings", "max"] = "5"; // unset reads as 0: the first warn is the last
            var flooder = chat.Add("Flooder");

            for (var i = 0; i < 8; i++) chat.Deliver(chat.Text(flooder, $"spam {i}"));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            Assert.AreEqual(MemberState.Member, State(chat, flooder));
            CollectionAssert.AreEqual(new[] { "standard add Flood" }, await WarnReasons(chat));
            Assert.AreEqual(0, chat.Telegram.MessagesFrom(chat.ChatId, flooder.Id).Count);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Flood_Tempban_IsATempbanForFlood()
        {
            await using var chat = await HarnessChat.StartAsync();
            FloodOn(chat, "tempban");
            var flooder = chat.Add("Flooder");

            for (var i = 0; i < 8; i++) chat.Deliver(chat.Text(flooder, $"spam {i}"));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            Assert.AreEqual(MemberState.Kicked, State(chat, flooder));
            var ban = (await Sanctions(chat)).Single();
            Assert.AreEqual(("tempban", "Flood"), (ban.Kind, ban.Reason));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Flood_ATypeOnTheExceptionList_IsNotCounted()
        {
            await using var chat = await HarnessChat.StartAsync();
            FloodOn(chat, "kick");
            // Inverted like the other toggles: "no" means "do not count this type".
            chat.Hashes[$"chat:{chat.ChatId}:floodexceptions", "image"] = "no";
            var poster = chat.Add("Poster");

            for (var i = 0; i < 8; i++) chat.Deliver(chat.Photo(poster, $"pic{i}"));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            Assert.AreEqual(MemberState.Member, State(chat, poster));
            Assert.AreEqual(8, chat.Telegram.MessagesFrom(chat.ChatId, poster.Id).Count);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Flood_AWatchedMember_IsExempt()
        {
            await using var chat = await HarnessChat.StartAsync();
            FloodOn(chat, "kick");
            var watched = chat.Add("Watched");
            chat.Keys.AddToSet($"chat:{chat.ChatId}:watch", watched.Id.ToString());

            for (var i = 0; i < 8; i++) chat.Deliver(chat.Text(watched, $"spam {i}"));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            Assert.AreEqual(MemberState.Member, State(chat, watched));
            AssertNoErrors(chat);
        }

        // ---------- Text length ----------

        private static void TextLength(HarnessChat chat, string action, int maxLength = 100, int maxLines = 50)
        {
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "enabled"] = "yes";
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "maxlength"] = maxLength.ToString();
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "maxlines"] = maxLines.ToString();
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "action"] = action;
        }

        [TestMethod]
        public async Task TextLength_Kick_DeletesTheMessage_KicksAndSaysSo()
        {
            await using var chat = await HarnessChat.StartAsync();
            TextLength(chat, "kick");
            var talker = chat.Add("Talker");

            await chat.SendAsync(talker, new string('a', 150));

            Assert.AreEqual(MemberState.Left, State(chat, talker));
            Assert.AreEqual(0, chat.Telegram.MessagesFrom(chat.ChatId, talker.Id).Count);
            Assert.AreEqual(("kick", "Long message"), ((await Sanctions(chat)).Single().Kind, (await Sanctions(chat)).Single().Reason));
            Assert.AreEqual(1, chat.Replies.Count(r => r.Contains(talker.Id.ToString())));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TextLength_Ban_Bans()
        {
            await using var chat = await HarnessChat.StartAsync();
            TextLength(chat, "ban");
            var talker = chat.Add("Talker");

            await chat.SendAsync(talker, new string('a', 150));

            Assert.AreEqual(MemberState.Kicked, State(chat, talker));
            Assert.AreEqual(("ban", "Long message"), ((await Sanctions(chat)).Single().Kind, (await Sanctions(chat)).Single().Reason));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TextLength_Warn_IsSpelledWithACapital_AndWarns()
        {
            await using var chat = await HarnessChat.StartAsync();
            // The length menus store "Warn", the flood menu "warn"; each check only knows its own.
            TextLength(chat, "Warn");
            chat.Hashes[$"chat:{chat.ChatId}:warnsettings", "max"] = "5";
            var talker = chat.Add("Talker");

            await chat.SendAsync(talker, new string('a', 150));

            Assert.AreEqual(MemberState.Member, State(chat, talker));
            CollectionAssert.AreEqual(new[] { "standard add Long message" }, await WarnReasons(chat));
            Assert.AreEqual(0, chat.Telegram.MessagesFrom(chat.ChatId, talker.Id).Count, "deleted as well");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TextLength_Tempban_Tempbans()
        {
            await using var chat = await HarnessChat.StartAsync();
            TextLength(chat, "tempban");
            var talker = chat.Add("Talker");

            await chat.SendAsync(talker, new string('a', 150));

            Assert.AreEqual(MemberState.Kicked, State(chat, talker));
            Assert.AreEqual("tempban", (await Sanctions(chat)).Single().Kind);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TextLength_TooManyLines_CountsToo()
        {
            await using var chat = await HarnessChat.StartAsync();
            TextLength(chat, "kick", maxLength: 1000, maxLines: 5);
            var talker = chat.Add("Talker");

            await chat.SendAsync(talker, string.Concat(Enumerable.Repeat("line\n", 10)));

            Assert.AreEqual(MemberState.Left, State(chat, talker));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TextLength_LinesAreCountedOnce()
        {
            await using var chat = await HarnessChat.StartAsync();
            TextLength(chat, "kick", maxLength: 1000, maxLines: 10);
            var talker = chat.Add("Talker");

            await chat.SendAsync(talker, string.Concat(Enumerable.Repeat("line\n", 6)) + "end");
            Assert.AreEqual(MemberState.Member, State(chat, talker), "7 lines against a limit of 10");
            await chat.SendAsync(talker, string.Concat(Enumerable.Repeat("line\n", 9)) + "end");

            Assert.AreEqual(MemberState.Left, State(chat, talker), "10 lines reach it, as a length equal to the limit does");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TextLength_UnderTheLimits_OrDisabled_LeavesItAlone()
        {
            await using var chat = await HarnessChat.StartAsync();
            TextLength(chat, "kick");
            var talker = chat.Add("Talker");

            await chat.SendAsync(talker, "short and sweet");
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "enabled"] = "no";
            await chat.AdvanceAsync(TimeSpan.FromSeconds(11)); // past the busy-group settings snapshot
            await chat.SendAsync(talker, new string('a', 150));

            Assert.AreEqual(MemberState.Member, State(chat, talker));
            Assert.AreEqual(2, chat.Telegram.MessagesFrom(chat.ChatId, talker.Id).Count);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TextLength_OnlyLooksAtText_NotCaptions()
        {
            await using var chat = await HarnessChat.StartAsync();
            TextLength(chat, "kick");
            var poster = chat.Add("Poster");

            await chat.PostAsync(chat.Photo(poster, "pic", caption: new string('a', 150)));

            Assert.AreEqual(MemberState.Member, State(chat, poster));
            AssertNoErrors(chat);
        }

        // ---------- Name length ----------

        [TestMethod]
        public async Task NameLength_ALongFirstAndLastName_IsKickedOnTheirNextMessage_WhichStays()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:antinamelengthsettings", "enabled"] = "yes";
            chat.Hashes[$"chat:{chat.ChatId}:antinamelengthsettings", "maxlength"] = "20";
            chat.Hashes[$"chat:{chat.ChatId}:antinamelengthsettings", "action"] = "kick";
            var longName = chat.Add(new string('n', 12), lastName: new string('m', 12));
            var shortName = chat.Add("Short", lastName: "Name");

            await chat.SendAsync(longName, "hi");
            await chat.SendAsync(shortName, "hi");

            Assert.AreEqual(MemberState.Left, State(chat, longName));
            Assert.AreEqual(MemberState.Member, State(chat, shortName));
            Assert.AreEqual(("kick", "Name too long"), ((await Sanctions(chat)).Single().Kind, (await Sanctions(chat)).Single().Reason));
            Assert.AreEqual(1, chat.Telegram.MessagesFrom(chat.ChatId, longName.Id).Count, "the name check does not delete the message");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Length_AMissingLimit_IsTheDefault_NotZero()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:antinamelengthsettings", "enabled"] = "yes";
            chat.Hashes[$"chat:{chat.ChatId}:antinamelengthsettings", "action"] = "kick";
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "enabled"] = "yes";
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "maxlength"] = "0";
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "action"] = "kick";
            var member = chat.Add("Bo");
            var longName = chat.Add(new string('n', 60));

            await chat.SendAsync(member, "hi");
            await chat.SendAsync(longName, "hi");

            Assert.AreEqual(MemberState.Member, State(chat, member), "a short name and a short text are fine");
            Assert.AreEqual(1, chat.Telegram.MessagesFrom(chat.ChatId, member.Id).Count);
            Assert.AreEqual(MemberState.Left, State(chat, longName), "over the default of 50");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task NameLength_Ban_Bans()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:antinamelengthsettings", "enabled"] = "yes";
            chat.Hashes[$"chat:{chat.ChatId}:antinamelengthsettings", "maxlength"] = "20";
            chat.Hashes[$"chat:{chat.ChatId}:antinamelengthsettings", "action"] = "ban";
            var longName = chat.Add(new string('n', 30));

            await chat.SendAsync(longName, "hi");

            Assert.AreEqual(MemberState.Kicked, State(chat, longName));
            Assert.AreEqual("Name too long", (await Sanctions(chat)).Single().Reason);
            AssertNoErrors(chat);
        }

        // ---------- Media locks ----------

        [TestMethod]
        public async Task Media_ABlockedType_IsDeletedWithAStrike_AndTheActionAtTheLimit()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:media", "image"] = "blocked";
            chat.Hashes[$"chat:{chat.ChatId}:media", "action"] = "kick";
            chat.Hashes[$"chat:{chat.ChatId}:warnsettings", "mediamax"] = "2";
            var poster = chat.Add("Poster");

            await chat.SendPhotoAsync(poster, "one");
            Assert.AreEqual(MemberState.Member, State(chat, poster));
            Assert.AreEqual(1, chat.Replies.Count, "a notice with the strike count");
            await chat.SendPhotoAsync(poster, "two");

            Assert.AreEqual(MemberState.Left, State(chat, poster));
            Assert.AreEqual(("kick", "Media"), ((await Sanctions(chat)).Single().Kind, (await Sanctions(chat)).Single().Reason));
            Assert.AreEqual(0, chat.Telegram.MessagesFrom(chat.ChatId, poster.Id).Count, "both deleted");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Media_LinksBlocked_DeletesALink_ButNotPlainText()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:media", "link"] = "blocked";
            chat.Hashes[$"chat:{chat.ChatId}:media", "action"] = "kick";
            chat.Hashes[$"chat:{chat.ChatId}:warnsettings", "mediamax"] = "3";
            var poster = chat.Add("Poster");

            var link = chat.Text(poster, "see example.com");
            link.Entities = new[] { new MessageEntity { Type = MessageEntityType.Url, Offset = 4, Length = 11 } };
            await chat.PostAsync(link);
            await chat.SendAsync(poster, "no link here");

            Assert.AreEqual(1, chat.Telegram.MessagesFrom(chat.ChatId, poster.Id).Count, "the link went, the text stayed");
            Assert.AreEqual(MemberState.Member, State(chat, poster));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Media_LinksBlocked_CatchesALinkInACaption()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:media", "link"] = "blocked";
            chat.Hashes[$"chat:{chat.ChatId}:media", "action"] = "kick";
            chat.Hashes[$"chat:{chat.ChatId}:warnsettings", "mediamax"] = "3";
            var poster = chat.Add("Poster");

            var linked = chat.Photo(poster, "a", caption: "join example.com");
            linked.CaptionEntities = new[] { new MessageEntity { Type = MessageEntityType.Url, Offset = 5, Length = 11 } };
            await chat.PostAsync(linked);
            await chat.PostAsync(chat.Photo(poster, "b", caption: "just a photo"));

            Assert.AreEqual(1, chat.Telegram.MessagesFrom(chat.ChatId, poster.Id).Count, "the captioned link went, the plain photo stayed");
            Assert.AreEqual("1", chat.Hashes[$"chat:{chat.ChatId}:mediawarn", poster.Id.ToString()], "one strike");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Media_AllowedTypes_AndWatchedMembers_AreLeftAlone()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:media", "image"] = "blocked";
            chat.Hashes[$"chat:{chat.ChatId}:media", "action"] = "kick";
            var watched = chat.Add("Watched");
            chat.Keys.AddToSet($"chat:{chat.ChatId}:watch", watched.Id.ToString());
            var poster = chat.Add("Poster");

            await chat.SendPhotoAsync(watched, "w");
            await chat.SendAsync(poster, "plain text");

            Assert.AreEqual(2, chat.Telegram.MessagesFrom(chat.ChatId, watched.Id).Count + chat.Telegram.MessagesFrom(chat.ChatId, poster.Id).Count);
            Assert.AreEqual(0, chat.Replies.Count);
            AssertNoErrors(chat);
        }

        // ---------- Arabic script and right-to-left ----------

        [TestMethod]
        public async Task Arab_Kick_InTheText()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:char", "Arab"] = "kick";
            var writer = chat.Add("Writer");

            await chat.SendAsync(writer, "سلام");

            Assert.AreEqual(MemberState.Left, State(chat, writer));
            Assert.AreEqual(("kick", "Non-Latin script"), ((await Sanctions(chat)).Single().Kind, (await Sanctions(chat)).Single().Reason));
            Assert.AreEqual(1, chat.Telegram.MessagesFrom(chat.ChatId, writer.Id).Count, "the message is not deleted");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Arab_Ban_InTheName()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:char", "Arab"] = "ban";
            var named = chat.Add("بدر");

            await chat.SendAsync(named, "hello");

            Assert.AreEqual(MemberState.Kicked, State(chat, named));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Arab_Kick_OnJoining_ByName()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:char", "Arab"] = "kick";
            var joiner = new HarnessUser(515151, "بدر");

            await chat.JoinAsync(joiner);

            Assert.AreEqual(MemberState.Left, State(chat, joiner));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Arab_OnJoining_EveryoneAddedIsCheckedAsThemselves_NotTheMemberWhoAddedThem()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:char", "Arab"] = "kick";
            var adder = chat.Add("Adder");
            var latin = chat.Add("Latin", MemberState.Left);
            var arabic = chat.Add("بدر", MemberState.Left);
            var watched = chat.Add("سلام", MemberState.Left);
            chat.Keys.AddToSet($"chat:{chat.ChatId}:watch", watched.Id.ToString());
            var join = chat.Join(latin, addedBy: adder);
            foreach (var u in new[] { arabic, watched }) chat.Telegram.SetMember(chat.ChatId, u.Id, MemberState.Member);
            join.NewChatMembers = new[] { latin.ToUser(), arabic.ToUser(), watched.ToUser() };

            await chat.PostAsync(join);

            Assert.AreEqual(MemberState.Member, State(chat, adder), "the adder is not the one with the name");
            Assert.AreEqual(MemberState.Member, State(chat, latin));
            Assert.AreEqual(MemberState.Left, State(chat, arabic), "the second person added is checked too");
            Assert.AreEqual(MemberState.Member, State(chat, watched), "a joiner on the watch list is exempt");
            Assert.AreEqual(arabic.Id, (await Sanctions(chat)).Single().Target);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Arab_OnlySomeLettersCount_AndAllowedOrWatchedIsLeftAlone()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:char", "Arab"] = "kick";
            var writer = chat.Add("Writer");
            var watched = chat.Add("Watched");
            chat.Keys.AddToSet($"chat:{chat.ChatId}:watch", watched.Id.ToString());

            // "ع" (ain) is not in the hand-written character class the check uses.
            await chat.SendAsync(writer, "ع");
            await chat.SendAsync(watched, "سلام");

            Assert.AreEqual(MemberState.Member, State(chat, writer));
            Assert.AreEqual(MemberState.Member, State(chat, watched));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Rtl_Ban_ForTheOverrideCharacterInTheText()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:char", "Rtl"] = "ban";
            var writer = chat.Add("Writer");

            await chat.SendAsync(writer, "abc\u202Edef");

            Assert.AreEqual(MemberState.Kicked, State(chat, writer));
            Assert.AreEqual(("ban", "Right-to-left text"), ((await Sanctions(chat)).Single().Kind, (await Sanctions(chat)).Single().Reason));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Rtl_Kick_ForTheOverrideCharacterInTheLastName()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:char", "Rtl"] = "kick";
            var writer = chat.Add("Writer", lastName: "x\u202Ey");

            await chat.SendAsync(writer, "hello");

            Assert.AreEqual(MemberState.Left, State(chat, writer));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Rtl_Tempban_IsNotActedOn()
        {
            // RightToLeft returns early unless the setting is "ban" or "kick", so its own tempban
            // branch is never reached. Pinned as found.
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:char", "Rtl"] = "tempban";
            var writer = chat.Add("Writer");

            await chat.SendAsync(writer, "abc\u202Edef");

            Assert.AreEqual(MemberState.Member, State(chat, writer));
            AssertNoErrors(chat);
        }

        // ---------- Admins ----------

        [TestMethod]
        public async Task Admins_AreExemptFromEveryAutomaticCheck()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:settings", "Flood"] = "no";
            chat.Hashes[$"chat:{chat.ChatId}:flood", "MaxFlood"] = "3";
            chat.Hashes[$"chat:{chat.ChatId}:flood", "ActionFlood"] = "kick";
            chat.Hashes[$"chat:{chat.ChatId}:media", "image"] = "blocked";
            chat.Hashes[$"chat:{chat.ChatId}:media", "action"] = "kick";
            chat.Hashes[$"chat:{chat.ChatId}:warnsettings", "mediamax"] = "1";
            TextLength(chat, "kick", maxLength: 20);
            chat.Hashes[$"chat:{chat.ChatId}:antinamelengthsettings", "enabled"] = "yes";
            chat.Hashes[$"chat:{chat.ChatId}:antinamelengthsettings", "maxlength"] = "5";
            chat.Hashes[$"chat:{chat.ChatId}:antinamelengthsettings", "action"] = "kick";
            chat.Hashes[$"chat:{chat.ChatId}:char", "Arab"] = "kick";
            chat.Hashes[$"chat:{chat.ChatId}:char", "Rtl"] = "kick";
            var admin = chat.Add("A very long admin name", MemberState.Administrator);

            await chat.SendPhotoAsync(admin, "pic");
            await chat.SendAsync(admin, new string('a', 50));
            await chat.SendAsync(admin, "سلام \u202E");
            for (var i = 0; i < 6; i++) chat.Deliver(chat.Text(admin, $"announcement {i}"));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(30));

            Assert.AreEqual(MemberState.Administrator, State(chat, admin));
            Assert.AreEqual(9, chat.Telegram.MessagesFrom(chat.ChatId, admin.Id).Count, "nothing deleted");
            Assert.AreEqual(0, chat.Telegram.Calls.Count(c => c.Method == "banChatMember"), "no kick tried");
            Assert.AreEqual(0, chat.Replies.Count, "no notices");
            Assert.AreEqual(0, chat.Hashes.All($"chat:{chat.ChatId}:mediawarn").Count, "no media strike");
            AssertNoErrors(chat);
        }

        // ---------- Blocked inline bots and sticker packs ----------

        [TestMethod]
        public async Task InlineBot_OnTheBlocklist_IsDeletedSilently_UnlessAnAdminSentIt()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Keys.AddToSet($"chat:{chat.ChatId}:blockedinline", "u:spambot");
            var member = chat.Add("Member");
            var admin = chat.Add("Admin", MemberState.Administrator);
            var spamBot = new User { Id = 909090, IsBot = true, FirstName = "Spam", Username = "SpamBot" };

            var fromMember = chat.Text(member, "via a bot");
            fromMember.ViaBot = spamBot;
            var fromAdmin = chat.Text(admin, "via a bot too");
            fromAdmin.ViaBot = spamBot;
            await chat.PostAsync(fromMember);
            await chat.PostAsync(fromAdmin);

            Assert.AreEqual(0, chat.Telegram.MessagesFrom(chat.ChatId, member.Id).Count);
            Assert.AreEqual(1, chat.Telegram.MessagesFrom(chat.ChatId, admin.Id).Count);
            Assert.AreEqual(MemberState.Member, State(chat, member), "deleted, nothing else");
            Assert.AreEqual(0, chat.Replies.Count);
            AssertNoErrors(chat);
        }

        private static Message Sticker(HarnessChat chat, HarnessUser from, string set) => new Message
        {
            Id = chat.Telegram.NextMessageId(),
            Chat = new Chat { Id = chat.ChatId, Type = ChatType.Supergroup, Title = chat.Title },
            From = from.ToUser(),
            Date = chat.Clock.UtcNow,
            Sticker = new Sticker { FileId = "st" + set, FileUniqueId = "stu" + set, Type = StickerType.Regular, Width = 512, Height = 512, SetName = set }
        };

        [TestMethod]
        public async Task StickerPack_OnTheBlocklist_IsDeletedSilently_WithoutAMediaStrike()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Keys.AddToSet($"chat:{chat.ChatId}:blockedstickersets", "badpack");
            chat.Hashes[$"chat:{chat.ChatId}:media", "sticker"] = "blocked";
            chat.Hashes[$"chat:{chat.ChatId}:media", "action"] = "kick";
            var member = chat.Add("Member");

            await chat.PostAsync(Sticker(chat, member, "BadPack"));
            await chat.PostAsync(Sticker(chat, member, "BadPack"));
            await chat.PostAsync(Sticker(chat, member, "BadPack"));

            Assert.AreEqual(0, chat.Telegram.MessagesFrom(chat.ChatId, member.Id).Count);
            Assert.AreEqual(MemberState.Member, State(chat, member), "the pack block skips the media check");
            Assert.AreEqual(0, chat.Replies.Count);
            AssertNoErrors(chat);
        }

        // ---------- Channel posts ----------

        [TestMethod]
        public async Task ChannelPosts_Off_DeletesThePost_AndBansTheChannel_ButNotTheLinkedOne()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:channelposts", "enabled"] = "yes";
            const long linked = -1009999000001, other = -1009999000002;
            chat.Telegram.SetLinkedChannel(chat.ChatId, linked);
            var placeholder = new HarnessUser(136817688, "Channel");

            Message AsChannel(long channelId, string text)
            {
                var m = chat.Text(placeholder, text);
                m.SenderChat = new Chat { Id = channelId, Type = ChatType.Channel, Title = "Chan" + channelId };
                return m;
            }

            await chat.PostAsync(AsChannel(other, "promo"));
            await chat.PostAsync(AsChannel(linked, "from the linked channel"));

            Assert.IsTrue(chat.Telegram.SenderChatBanned(chat.ChatId, other));
            Assert.IsFalse(chat.Telegram.SenderChatBanned(chat.ChatId, linked));
            Assert.AreEqual(1, Deletes(chat));
            AssertNoErrors(chat);
        }

        // The premium mass-add rule (two hardcoded groups, UpdateHandler.HandleNewChatMembers) is not
        // pinned here: it calls Commands.Tempban synchronously on the update consumer, which blocks on
        // the enforcement queue's wait and so on the simulated clock, and the harness cannot advance
        // time under a blocked consumer. The port (build step 4) dispatches it and adds its scenario.
    }
}
