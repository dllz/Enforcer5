using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Detection step 9: the message signs, about new members only. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Detection")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class MessageSignScenarios
    {
        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static readonly string[] MessageSignNames =
            { "link_outside", "mention_outside", "forward_channel", "first_post_media", "posted_soon", "same_as_newcomers", "joined_during_raid" };

        private static async Task<(long User, string Sign, string Mode)[]> Signs(HarnessChat chat)
        {
            await DetectionRecorder.FlushAsync();
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<(long, string, string)>(@"
SELECT a.telegram_user_id, d.sign, d.mode FROM detections d JOIN telegram_accounts a ON a.account_id = d.account_id
WHERE d.sign = ANY(@Signs) ORDER BY d.detection_id", new { Signs = MessageSignNames })).ToArray();
        }

        private static string[] SignsOf(( long User, string Sign, string Mode)[] rows, HarnessUser user) =>
            rows.Where(r => r.User == user.Id).Select(r => r.Sign).OrderBy(s => s).ToArray();

        private static Task GoLive(HarnessChat chat) =>
            chat.ChangeSettingsAsync(p => p with { Engine = p.Engine with { ObserveUntil = chat.Clock.GetUtcNow().AddMinutes(-1) } });

        /// <summary>A text with its links and @mentions marked, as Telegram sends them.</summary>
        private static Message Marked(HarnessChat chat, HarnessUser from, string text)
        {
            var message = chat.Text(from, text);
            message.Entities = Regex.Matches(text, @"@\w+").Select(m => new MessageEntity { Type = MessageEntityType.Mention, Offset = m.Index, Length = m.Length })
                .Concat(Regex.Matches(text, @"(https?://|t\.me/)\S+").Select(m => new MessageEntity { Type = MessageEntityType.Url, Offset = m.Index, Length = m.Length }))
                .ToArray();
            return message;
        }

        private static HarnessUser Established(HarnessChat chat, string name)
        {
            var user = chat.Add(name);
            chat.Hashes[$"{chat.ChatId}:users:{user.Id}", "msgs"] = "5000";
            return user;
        }

        [TestMethod]
        public async Task ANewcomersLink_RightAfterJoining_AddsUpToAReport_AnEstablishedMembersLinkIsNothing()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var newcomer = chat.Add("Newcomer");
            var regular = Established(chat, "Regular");
            await chat.JoinAsync(newcomer);
            await chat.AdvanceAsync(TimeSpan.FromSeconds(10));

            await chat.PostAsync(Marked(chat, newcomer, "great deals at https://shop.example"));
            await chat.PostAsync(Marked(chat, regular, "the rules are at https://example.org"));

            var rows = await Signs(chat);
            CollectionAssert.AreEqual(new[] { "link_outside", "posted_soon" }, SignsOf(rows, newcomer));
            Assert.AreEqual(0, SignsOf(rows, regular).Length);
            Assert.IsTrue(rows.All(r => r.Mode == "live"));
            var report = chat.Telegram.SentMessages.Single(m => m.Chat.Id == admin.Id && m.Text?.StartsWith("🛡") == true);
            StringAssert.Contains(report.Text, "link to another chat or website");
            StringAssert.Contains(report.Text, "posted right after joining");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task WhileWatching_TheSignsAreRecorded_AndNobodyIsTold()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var newcomer = chat.Add("Newcomer");
            await chat.JoinAsync(newcomer);

            await chat.PostAsync(Marked(chat, newcomer, "https://shop.example"));

            Assert.IsTrue((await Signs(chat)).All(r => r.Mode == "observe"));
            Assert.AreEqual(2, (await Signs(chat)).Length);
            Assert.IsFalse(chat.Telegram.SentMessages.Any(m => m.Chat.Id == admin.Id && m.Text?.StartsWith("🛡") == true));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task LinksToThisGroup_AndMentionsOfMembers_AreNotSigns_AnOutsideChannelIs()
        {
            await using var chat = await HarnessChat.StartAsync(username: "harnessgroup");
            var friend = Established(chat, "Friend");
            chat.Hashes[$"{chat.ChatId}:users:{friend.Id}", "msgs"] = "5000";
            chat.Hashes["bot:usernames", "@friend"] = friend.Id.ToString();
            chat.Telegram.AddPublicChat("cryptogems", -1007777);
            var newcomer = chat.Add("Newcomer");

            await chat.SendAsync(newcomer, "hi");
            await chat.PostAsync(Marked(chat, newcomer, "see t.me/harnessgroup/5 and ask @friend"));
            Assert.AreEqual(0, (await Signs(chat)).Length);

            await chat.PostAsync(Marked(chat, newcomer, "join @cryptogems"));
            CollectionAssert.AreEqual(new[] { "mention_outside" }, SignsOf(await Signs(chat), newcomer));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AForwardFromAChannel_IsASign_UnlessItIsTheGroupsOwnChannel()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Telegram.SetLinkedChannel(chat.ChatId, -1005555);
            var newcomer = chat.Add("Newcomer");
            await chat.SendAsync(newcomer, "hi");
            Message Forward(long channel)
            {
                var message = chat.Text(newcomer, "news");
                message.ForwardOrigin = new MessageOriginChannel { Chat = new Chat { Id = channel, Type = ChatType.Channel, Title = "c" }, MessageId = 1 };
                return message;
            }

            await chat.PostAsync(Forward(-1005555));
            Assert.AreEqual(0, (await Signs(chat)).Length);
            await chat.PostAsync(Forward(-1006666));
            CollectionAssert.AreEqual(new[] { "forward_channel" }, SignsOf(await Signs(chat), newcomer));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task OnlyTheFirstPost_CanBeAFirstPostOfMedia()
        {
            await using var chat = await HarnessChat.StartAsync();
            var newcomer = chat.Add("Newcomer");
            var other = chat.Add("Other");

            await chat.PostAsync(chat.Photo(newcomer, "one"));
            await chat.PostAsync(chat.Photo(newcomer, "two"));
            await chat.SendAsync(other, "hi");
            await chat.PostAsync(chat.Photo(other, "three"));

            var rows = await Signs(chat);
            CollectionAssert.AreEqual(new[] { "first_post_media" }, SignsOf(rows, newcomer));
            Assert.AreEqual(0, SignsOf(rows, other).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheSameLongText_FromTwoNewcomers_IsASignForBoth_AGreetingIsNot()
        {
            await using var chat = await HarnessChat.StartAsync();
            var first = chat.Add("First");
            var second = chat.Add("Second");
            var third = chat.Add("Third");
            foreach (var u in new[] { first, second, third }) await chat.SendAsync(u, "hello");
            Assert.AreEqual(0, (await Signs(chat)).Length, "hello from everyone");

            await chat.SendAsync(first, "Earn five hundred dollars a day from home, ask me how");
            await chat.AdvanceAsync(TimeSpan.FromHours(3));
            await chat.SendAsync(second, "Earn five hundred dollars a day from home, ask me how!!");

            var rows = await Signs(chat);
            CollectionAssert.AreEqual(new[] { "same_as_newcomers" }, SignsOf(rows, first));
            CollectionAssert.AreEqual(new[] { "same_as_newcomers" }, SignsOf(rows, second));
            Assert.AreEqual(0, SignsOf(rows, third).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task JoiningDuringARaid_IsASign()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            Enforcer5.Raids.RaidGuard.Activate(chat.ChatId, Enforcer5.Raids.RaidActions.Quarantine, "command", admin.Id);
            await chat.SettleAsync();
            var joiner = chat.Add("Joiner");

            await chat.JoinAsync(joiner);

            CollectionAssert.AreEqual(new[] { "joined_during_raid" }, SignsOf(await Signs(chat), joiner));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnEditThatAddsALink_IsALinkSign()
        {
            await using var chat = await HarnessChat.StartAsync();
            var newcomer = chat.Add("Newcomer");
            await chat.SendAsync(newcomer, "hi");
            var post = await chat.SendAsync(newcomer, "nice group");

            await chat.EditAsync(post, Marked(chat, newcomer, "nice group https://shop.example"));

            CollectionAssert.AreEqual(new[] { "link_outside" }, SignsOf(await Signs(chat), newcomer));
            AssertNoErrors(chat);
        }
    }
}
