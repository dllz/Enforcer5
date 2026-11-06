using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Other bots in a group (Detection/BotGuard). On 2026-10-31 a member mentioned three guest bots (Bot API 10.0)
    /// once a minute in the support group; each answered with spam it could post without joining, and the bot
    /// ignored every bot's message. Guest bots summoned by members are removed now, bots added by members banned,
    /// every add logged with who did it, and other bots' posts checked like a member's. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class BotScenarios
    {
        private const long LogChannel = -1005555;

        private sealed class BotRow
        {
            public long BotId { get; set; }
            public string Status { get; set; }
            public string Source { get; set; }
            public long? SetBy { get; set; }
        }

        private sealed class EventRow
        {
            public long BotId { get; set; }
            public long? ActorId { get; set; }
            public bool ActorWasAdmin { get; set; }
            public string Kind { get; set; }
            public string Action { get; set; }
        }

        private static async Task<BotRow[]> GroupBots(HarnessChat chat)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<BotRow>(@"
SELECT a.telegram_user_id AS BotId, x.status AS Status, x.source AS Source, b.telegram_user_id AS SetBy
FROM group_bots x JOIN telegram_accounts a ON a.account_id = x.account_id
LEFT JOIN telegram_accounts b ON b.account_id = x.set_by_account_id ORDER BY a.telegram_user_id")).ToArray();
        }

        private static async Task<EventRow[]> Events(HarnessChat chat)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<EventRow>(@"
SELECT a.telegram_user_id AS BotId, b.telegram_user_id AS ActorId, x.actor_was_admin AS ActorWasAdmin, x.kind AS Kind, x.action AS Action
FROM bot_events x JOIN telegram_accounts a ON a.account_id = x.bot_account_id
LEFT JOIN telegram_accounts b ON b.account_id = x.actor_account_id ORDER BY x.event_id")).ToArray();
        }

        private static async Task<string[]> Signs(HarnessChat chat, HarnessUser user)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<string>(@"
SELECT d.sign FROM detections d JOIN telegram_accounts a ON a.account_id = d.account_id
WHERE a.telegram_user_id = @Id ORDER BY d.detection_id", new { user.Id })).ToArray();
        }

        private static async Task<HarnessChat> StartAsync()
        {
            var chat = await HarnessChat.StartAsync();
            chat.Keys.AddToSet("logChatGroups", chat.ChatId.ToString());
            chat.Hashes[$"chat:{chat.ChatId}:settings", "logchat"] = LogChannel.ToString();
            return chat;
        }

        private static string[] Logs(HarnessChat chat) =>
            chat.Telegram.SentMessages.Where(m => m.Chat.Id == LogChannel).Select(m => m.Text).ToArray();

        /// <summary>Log posts split into entries: the log queue merges a backlog into one post, a blank line between entries.</summary>
        private static string[] LogEntries(HarnessChat chat) => Logs(chat).SelectMany(l => l.Split("\n\n")).ToArray();

        private static MemberState State(HarnessChat chat, HarnessUser user) => chat.Telegram.GetMember(chat.ChatId, user.Id);

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        /// <summary>A guest bot's answer to <paramref name="summoning"/>, as Telegram marks it.</summary>
        internal static Message GuestReply(HarnessChat chat, HarnessUser bot, HarnessUser caller, Message summoning, string text = "Cheap followers, DM me")
        {
            var reply = chat.Text(bot, text, summoning);
            reply.GuestBotCallerUser = caller.ToUser();
            reply.GuestQueryId = $"guest{reply.MessageId}";
            return reply;
        }

        [TestMethod]
        public async Task AGuestBotSummonedByAMember_IsRemoved_AndTheMemberReported()
        {
            await using var chat = await StartAsync();
            chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Whoever");
            var bots = Enumerable.Range(0, 3).Select(i => chat.Add($"Spam {i}", MemberState.Left, username: $"spam{i}bot", isBot: true)).ToArray();

            var summoning = await chat.SendAsync(member, "/start 10 @spam0bot @spam1bot @spam2bot");
            foreach (var bot in bots) chat.Deliver(GuestReply(chat, bot, member, summoning));
            await chat.CompleteAsync();

            foreach (var bot in bots)
            {
                Assert.AreEqual(0, chat.Telegram.MessagesFrom(chat.ChatId, bot.Id).Count, "every answer deleted");
                Assert.AreEqual(MemberState.Kicked, State(chat, bot), "and every bot banned");
            }
            Assert.AreEqual(0, chat.Telegram.MessagesFrom(chat.ChatId, member.Id).Count, "the summoning post too");
            Assert.AreEqual(MemberState.Member, State(chat, member), "the member stays, for the admins to judge");
            CollectionAssert.Contains(await Signs(chat, member), SignCatalog.SummonedBot.Name);
            Assert.IsTrue((await GroupBots(chat)).All(b => b.Status == "blocked" && b.Source == "summoned" && b.SetBy == member.Id));
            Assert.AreEqual(3, (await Events(chat)).Count(e => e.Kind == "summoned" && e.ActorId == member.Id && e.Action == "banned"));
            Assert.AreEqual(3, LogEntries(chat).Count(l => l.Contains("🤖") && l.Contains(member.Id.ToString())), "each bot logged once");
            Assert.AreEqual(1, (await Signs(chat, member)).Count(s => s == SignCatalog.SummonedBot.Name), "one sign for one post");

            // The next wave: deleted again, without another ban or log line.
            var again = await chat.SendAsync(member, "/start 11 @spam0bot");
            await chat.PostAsync(GuestReply(chat, bots[0], member, again));
            Assert.AreEqual(0, chat.Telegram.MessagesFrom(chat.ChatId, bots[0].Id).Count);
            Assert.AreEqual(1, chat.Telegram.Accepted.Count(c => c.Method == "banChatMember" && c.UserId == bots[0].Id));
            Assert.AreEqual(3, LogEntries(chat).Count(l => l.Contains("🤖")));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AGuestBotSummonedByAnAdmin_IsLetThrough()
        {
            await using var chat = await StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var bot = chat.Add("Helper", MemberState.Left, username: "helperbot", isBot: true);

            var summoning = await chat.SendAsync(admin, "@helperbot translate this");
            await chat.PostAsync(GuestReply(chat, bot, admin, summoning, "Translated"));

            Assert.AreEqual(1, chat.Telegram.MessagesFrom(chat.ChatId, bot.Id).Count);
            Assert.AreNotEqual(MemberState.Kicked, State(chat, bot));
            Assert.AreEqual(0, Logs(chat).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AGuestAnswerOurServerDidNotMark_IsKnownByNotBeingAMember()
        {
            // A Bot API server older than 10.0 sends the answer without the caller: the bot is not in the group,
            // and it replies to the member who mentioned it.
            await using var chat = await StartAsync();
            var member = chat.Add("Whoever");
            var bot = chat.Add("Spam", MemberState.Left, username: "spambot", isBot: true);

            var summoning = await chat.SendAsync(member, "/start @spambot");
            await chat.PostAsync(chat.Text(bot, "Buy now", summoning));

            Assert.AreEqual(0, chat.Telegram.MessagesFrom(chat.ChatId, bot.Id).Count);
            Assert.AreEqual(MemberState.Kicked, State(chat, bot));
            CollectionAssert.Contains(await Signs(chat, member), SignCatalog.SummonedBot.Name);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task OurOwnBots_AreNeverTouched()
        {
            await using var chat = await StartAsync();
            var member = chat.Add("Player");
            var werewolf = chat.Add("Werewolf", MemberState.Left, username: "werewolfbot", isBot: true);

            var summoning = await chat.SendAsync(member, "/start@werewolfbot");
            await chat.PostAsync(GuestReply(chat, werewolf, member, summoning, "Game starting"));
            chat.Deliver(chat.Join(werewolf, addedBy: member));
            await chat.CompleteAsync();

            Assert.AreEqual(1, chat.Telegram.MessagesFrom(chat.ChatId, werewolf.Id).Count);
            Assert.AreEqual(MemberState.Member, State(chat, werewolf));
            Assert.AreEqual(0, (await GroupBots(chat)).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ABotAddedByAMember_IsBanned_LoggedWithWhoAddedIt_AndTheMemberReported()
        {
            await using var chat = await StartAsync();
            chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Adder");
            var bot = chat.Add("Spam", MemberState.Left, username: "addedspambot", isBot: true);

            // Telegram tells of it twice: the service message and the member update. One add.
            chat.Deliver(chat.Join(bot, addedBy: member));
            chat.Deliver(chat.MemberJoined(bot, addedBy: member));
            await chat.CompleteAsync();

            Assert.AreEqual(MemberState.Kicked, State(chat, bot));
            var log = Logs(chat).Single(l => l.Contains("addedspambot"));
            StringAssert.Contains(log, member.Id.ToString());
            var row = (await GroupBots(chat)).Single();
            Assert.AreEqual(("blocked", "member_added", (long?)member.Id), (row.Status, row.Source, row.SetBy));
            var added = (await Events(chat)).Single();
            Assert.AreEqual(("added", "banned", false, (long?)member.Id), (added.Kind, added.Action, added.ActorWasAdmin, added.ActorId));
            CollectionAssert.Contains(await Signs(chat, member), SignCatalog.AddedBot.Name);

            // Anything it gets in before the ban is deleted.
            await chat.SendAsync(bot, "hello");
            Assert.AreEqual(0, chat.Telegram.MessagesFrom(chat.ChatId, bot.Id).Count);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ABotAddedByAnAdmin_IsTrusted_AndLogged()
        {
            await using var chat = await StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var bot = chat.Add("Quiz", MemberState.Left, username: "quizbot", isBot: true);

            await chat.PostAsync(chat.Join(bot, addedBy: admin));
            await chat.SendAsync(bot, "Question 1");

            Assert.AreEqual(MemberState.Member, State(chat, bot));
            Assert.AreEqual(1, chat.Telegram.MessagesFrom(chat.ChatId, bot.Id).Count);
            StringAssert.Contains(Logs(chat).Single(l => l.Contains("quizbot")), admin.Id.ToString());
            Assert.AreEqual(("allowed", "admin_added"), ((await GroupBots(chat)).Single().Status, (await GroupBots(chat)).Single().Source));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ABotFromBefore_IsLeftBe_ButBreaksRulesLikeAMember_FloodAside()
        {
            await using var chat = await StartAsync();
            var bot = chat.Add("Game", username: "somegamebot", isBot: true); // in the group, no record of its add
            chat.Hashes[$"chat:{chat.ChatId}:settings", "Flood"] = "no";
            chat.Hashes[$"chat:{chat.ChatId}:flood", "MaxFlood"] = "4";
            chat.Hashes[$"chat:{chat.ChatId}:flood", "ActionFlood"] = "kick";

            for (var i = 0; i < 10; i++) chat.Deliver(chat.Text(bot, $"Round {i}"));
            await chat.CompleteAsync();
            Assert.AreEqual(MemberState.Member, State(chat, bot), "a game posts fast: no flood for bots");
            Assert.AreEqual(10, chat.Telegram.MessagesFrom(chat.ChatId, bot.Id).Count);
            Assert.AreEqual(("allowed", "present"), ((await GroupBots(chat)).Single().Status, (await GroupBots(chat)).Single().Source));

            // A message too long for the group: dealt with as a member's would be.
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "enabled"] = "yes";
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "maxlength"] = "100";
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "maxlines"] = "50";
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "action"] = "kick";
            await chat.ChangeSettingsAsync(p => p with { TextLength = new LengthSettings(true, 100, 50, "kick") });
            var longPost = await chat.SendAsync(bot, new string('a', 150));
            Assert.IsFalse(chat.Telegram.MessagesFrom(chat.ChatId, bot.Id).Contains(longPost.MessageId));
            Assert.AreEqual(MemberState.Left, State(chat, bot));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnotherBotsCommands_StillGetNoAnswer()
        {
            await using var chat = await StartAsync();
            var bot = chat.Add("Some bot", username: "somebot", isBot: true);
            var before = chat.Telegram.SentMessages.Count;
            await chat.SendAsync(bot, "/id");
            await chat.SendAsync(bot, "#rules");
            Assert.AreEqual(before, chat.Telegram.SentMessages.Count);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TrustBot_LetsAGuestBotAnswerMembers_AndUntrustBot_RemovesIt()
        {
            await using var chat = await StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var bot = chat.Add("Helper", MemberState.Left, username: "helperbot", isBot: true);

            // Removed first, summoned by a member; an admin summons it to reply to it.
            await chat.PostAsync(GuestReply(chat, bot, member, await chat.SendAsync(member, "@helperbot hi")));
            Assert.AreEqual(MemberState.Kicked, State(chat, bot));
            var own = await chat.PostAsync(GuestReply(chat, bot, admin, await chat.SendAsync(admin, "@helperbot hi"), "Hello admin"));
            await chat.SendAsync(admin, "/trustbot", own);
            Assert.AreNotEqual(MemberState.Kicked, State(chat, bot), "our ban is lifted");
            Assert.AreEqual(("allowed", "command"), ((await GroupBots(chat)).Single().Status, (await GroupBots(chat)).Single().Source));

            var answer = await chat.PostAsync(GuestReply(chat, bot, member, await chat.SendAsync(member, "@helperbot hi"), "Hello"));
            CollectionAssert.Contains(chat.Telegram.MessagesFrom(chat.ChatId, bot.Id).ToList(), answer.MessageId);
            StringAssert.Contains(chat.Replies.Last(), "helperbot");

            await chat.SendAsync(admin, "/trustedbots");
            StringAssert.Contains(chat.Replies.Last(), bot.Id.ToString());

            await chat.SendAsync(admin, "/untrustbot", answer);
            Assert.AreEqual(MemberState.Kicked, State(chat, bot));
            var later = await chat.PostAsync(GuestReply(chat, bot, admin, await chat.SendAsync(admin, "@helperbot hi"), "Hi"));
            Assert.IsFalse(chat.Telegram.MessagesFrom(chat.ChatId, bot.Id).Contains(later.MessageId), "removed even when an admin calls it");
            CollectionAssert.AreEqual(new[] { "summoned", "trusted", "untrusted" }, (await Events(chat)).Select(e => e.Kind).ToArray());
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TrustBot_RefusesAPerson()
        {
            await using var chat = await StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            await chat.SendAsync(admin, "/trustbot", await chat.SendAsync(member, "hi"));
            Assert.AreEqual(0, (await GroupBots(chat)).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnAdminBot_IsLeftAlone()
        {
            await using var chat = await StartAsync();
            var member = chat.Add("Member");
            var bot = chat.Add("Admin bot", MemberState.Administrator, username: "groupadminbot", isBot: true);
            await chat.SendAsync(bot, new string('a', 5000));
            await chat.PostAsync(GuestReply(chat, bot, member, await chat.SendAsync(member, "@groupadminbot")));
            Assert.AreEqual(2, chat.Telegram.MessagesFrom(chat.ChatId, bot.Id).Count);
            Assert.AreEqual(0, (await GroupBots(chat)).Length);
            AssertNoErrors(chat);
        }

        // Bots trusted in every group (Daniel, 2026-11-02): by a dev, or by the admins of a premium group.

        private static HarnessUser Dev(HarnessChat chat) => chat.Add("Dev", id: Models.Constants.Devs[0]);

        private static async Task<(string Status, long? SetBy)[]> GlobalRows(HarnessChat chat)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<(string, long?)>(@"
SELECT x.status, b.telegram_user_id FROM global_bots x
LEFT JOIN telegram_accounts b ON b.account_id = x.set_by_account_id")).ToArray();
        }

        [TestMethod]
        public async Task ABotADevTrusts_IsLetBeEverywhere_Unchecked_AndOurBansLifted()
        {
            await using var chat = await StartAsync();
            var dev = Dev(chat);
            var member = chat.Add("Member");
            var bot = chat.Add("Helper", MemberState.Left, username: "helperbot", isBot: true);

            // Banned first: a member summoned it.
            await chat.PostAsync(GuestReply(chat, bot, member, await chat.SendAsync(member, "@helperbot hi")));
            Assert.AreEqual(MemberState.Kicked, State(chat, bot));

            await chat.SendAsync(dev, $"/globaltrustbot {bot.Id}");
            await chat.CompleteAsync();
            Assert.AreEqual(("allowed", (long?)dev.Id), (await GlobalRows(chat)).Single());
            Assert.AreNotEqual(MemberState.Kicked, State(chat, bot), "our ban is lifted");
            Assert.AreEqual(("allowed", "global"), ((await GroupBots(chat)).Single().Status, (await GroupBots(chat)).Single().Source));

            // A member may summon it now, and add it; its posts are not checked (too long for the group here).
            await chat.ChangeSettingsAsync(p => p with { TextLength = new LengthSettings(true, 100, 50, "kick") });
            var answer = await chat.PostAsync(GuestReply(chat, bot, member, await chat.SendAsync(member, "@helperbot hi"), new string('a', 150)));
            CollectionAssert.Contains(chat.Telegram.MessagesFrom(chat.ChatId, bot.Id).ToList(), answer.MessageId);
            chat.Telegram.SetMember(chat.ChatId, bot.Id, MemberState.Left);
            await chat.PostAsync(chat.Join(bot, addedBy: member));
            Assert.AreEqual(MemberState.Member, State(chat, bot));
            StringAssert.Contains(Logs(chat).Last(), "trusted in every group");
            Assert.AreEqual(1, (await Signs(chat, member)).Count(x => x == SignCatalog.SummonedBot.Name), "only the first summoning gave a sign");

            await chat.SendAsync(dev, "/globalbots");
            StringAssert.Contains(chat.Replies.Last(), bot.Id.ToString());
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AGroupsOwnUntrust_BeatsTrustFromOutside()
        {
            await using var chat = await StartAsync();
            var dev = Dev(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var bot = chat.Add("Helper", MemberState.Left, username: "helperbot", isBot: true);
            await chat.SendAsync(dev, $"/globaltrustbot {bot.Id}");
            await chat.SendAsync(admin, $"/untrustbot {bot.Id}");
            Assert.AreEqual(MemberState.Kicked, State(chat, bot));

            var answer = await chat.PostAsync(GuestReply(chat, bot, member, await chat.SendAsync(member, "@helperbot hi")));
            Assert.IsFalse(chat.Telegram.MessagesFrom(chat.ChatId, bot.Id).Contains(answer.MessageId));
            await BotGuard.ReloadAsync();
            await chat.CompleteAsync();
            Assert.AreEqual(MemberState.Kicked, State(chat, bot), "a ban the admins chose is not lifted");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ABotADevBlocks_IsRemovedEverywhere_EvenWhereTrusted()
        {
            await using var chat = await StartAsync();
            var dev = Dev(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var bot = chat.Add("Quiz", MemberState.Left, username: "quizbot", isBot: true);
            await chat.PostAsync(chat.Join(bot, addedBy: admin));
            await chat.SendAsync(bot, "Question 1");
            Assert.AreEqual(1, chat.Telegram.MessagesFrom(chat.ChatId, bot.Id).Count);

            await chat.SendAsync(dev, $"/globaluntrustbot {bot.Id}");
            await chat.SendAsync(bot, "Question 2");
            Assert.AreEqual(1, chat.Telegram.MessagesFrom(chat.ChatId, bot.Id).Count, "deleted");
            Assert.AreEqual(MemberState.Kicked, State(chat, bot));
            Assert.AreEqual(1, Logs(chat).Count(l => l.Contains("blocked in every group")));

            // Added again, even by an admin: banned.
            chat.Telegram.SetMember(chat.ChatId, bot.Id, MemberState.Left);
            await chat.AdvanceAsync(BotGuard.SameJoin);
            await chat.PostAsync(chat.Join(bot, addedBy: admin));
            Assert.AreEqual(MemberState.Kicked, State(chat, bot));

            // Forgotten: the group's own decision counts again.
            await chat.SendAsync(dev, $"/globalforgetbot {bot.Id}");
            Assert.AreEqual(0, (await GlobalRows(chat)).Length);
            Assert.IsFalse(BotGuard.IsGloballyBlocked(bot.Id));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ABotAPremiumGroupTrusts_IsTrustedEverywhere_UntilTheGroupIsNoLongerPremium()
        {
            await using var premium = await StartAsync();
            var other = premium.Group(-100777);
            await premium.RoleAsync(Enforcer5.Data.RoleList.Premium, premium.ChatId);
            var admin = premium.Add("Admin", MemberState.Administrator);
            var member = other.Add("Member");
            var bot = premium.Add("Helper", MemberState.Left, username: "helperbot", isBot: true);
            other.Telegram.SetMember(other.ChatId, bot.Id, MemberState.Left);
            await BotGuard.ReloadAsync();

            // Banned in the other group first, summoned by a member there.
            await other.PostAsync(GuestReply(other, bot, member, await other.SendAsync(member, "@helperbot hi")));
            Assert.AreEqual(MemberState.Kicked, other.Telegram.GetMember(other.ChatId, bot.Id));

            await premium.SendAsync(admin, $"/trustbot {bot.Id}");
            Assert.IsTrue(BotGuard.IsTrustedEverywhere(bot.Id), "at once");
            await BotGuard.ReloadAsync();
            await other.CompleteAsync();
            Assert.AreNotEqual(MemberState.Kicked, other.Telegram.GetMember(other.ChatId, bot.Id), "our ban there is lifted");

            var answer = await other.PostAsync(GuestReply(other, bot, member, await other.SendAsync(member, "@helperbot hi"), "Hello"));
            CollectionAssert.Contains(other.Telegram.MessagesFrom(other.ChatId, bot.Id).ToList(), answer.MessageId);

            // A bot merely found in a premium group is not trusted elsewhere.
            var present = premium.Add("Game", username: "somegamebot", isBot: true);
            await premium.SendAsync(present, "Round 1");
            await BotGuard.ReloadAsync();
            Assert.IsFalse(BotGuard.IsTrustedEverywhere(present.Id));

            await premium.RoleAsync(Enforcer5.Data.RoleList.Premium, premium.ChatId, false);
            await BotGuard.ReloadAsync();
            Assert.IsFalse(BotGuard.IsTrustedEverywhere(bot.Id), "no longer premium");
            var later = await other.PostAsync(GuestReply(other, bot, member, await other.SendAsync(member, "@helperbot hi"), "Hello"));
            Assert.IsFalse(other.Telegram.MessagesFrom(other.ChatId, bot.Id).Contains(later.MessageId));
            AssertNoErrors(premium);
        }
    }
}
