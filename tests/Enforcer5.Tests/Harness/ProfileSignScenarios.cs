using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Data;
using Enforcer5.Detection;
using Enforcer5.Raids;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Detection step 11: the profile and channel lookups, the bio signs and the filter rules on bios and channels.
    /// Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Detection")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class ProfileSignScenarios
    {
        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static readonly string[] ProfileSignNames = { "bio_link", "bio_mention", "personal_channel" };

        private static async Task<(long User, string Sign, string Mode)[]> Signs(HarnessChat chat, params string[] signs)
        {
            await DetectionRecorder.FlushAsync();
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<(long, string, string)>(@"
SELECT a.telegram_user_id, d.sign, d.mode FROM detections d JOIN telegram_accounts a ON a.account_id = d.account_id
WHERE d.sign = ANY(@Signs) OR (@Filters AND d.sign LIKE 'filter:%') ORDER BY d.detection_id",
                new { Signs = signs.Length > 0 ? signs : ProfileSignNames, Filters = signs.Length == 0 })).ToArray();
        }

        private static string[] SignsOf((long User, string Sign, string Mode)[] rows, HarnessUser user) =>
            rows.Where(r => r.User == user.Id).Select(r => r.Sign).OrderBy(s => s).ToArray();

        private static Task GoLive(HarnessChat chat) =>
            chat.ChangeSettingsAsync(p => p with { Engine = p.Engine with { ObserveUntil = chat.Clock.GetUtcNow().AddMinutes(-1) } });

        private static int GetChatCalls(HarnessChat chat, long id) =>
            chat.Telegram.Calls.Count(c => c.Method == "getChat" && c.ChatId == id);

        /// <summary>An own rule, live from the start.</summary>
        private static async Task<FilterRule> LiveRule(HarnessChat chat, HarnessUser admin, string target, string action, string pattern)
        {
            await chat.SendAsync(admin, $"/filter add {target} {action} {pattern}");
            var rule = (await new PostgresDetectionSettingsRepository(() => chat.Database).LoadAsync(chat.ChatId))
                .Filters.OwnRules.Single(r => r.Target == target && r.Pattern == pattern);
            await chat.AdvanceAsync(TimeSpan.FromSeconds(20)); // the command limiter
            await chat.SendAsync(admin, $"/filter live {rule.Id}");
            await chat.AdvanceAsync(TimeSpan.FromSeconds(20));
            return rule;
        }

        [TestMethod]
        public async Task ANewcomersBio_IsLookedUpOnTheirFirstPost_AndOnlyFlagsAreKept()
        {
            await using var chat = await HarnessChat.StartAsync(username: "harnessgroup");
            await GoLive(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            chat.Telegram.AddChannel(-1008881, "Gems daily", username: "gemsdaily");
            var newcomer = chat.Add("Newcomer");
            chat.Telegram.SetProfile(newcomer.Id, "Earn 500$ a day: https://pump.example and @gemsdaily", personalChannel: -1008881);
            var friend = chat.Add("Friend");
            chat.Telegram.SetProfile(friend.Id, "Rules: t.me/harnessgroup");

            await chat.SendAsync(newcomer, "hello");
            await chat.SendAsync(newcomer, "anyone here?");
            await chat.SendAsync(friend, "hi");

            var rows = await Signs(chat);
            CollectionAssert.AreEqual(new[] { "bio_link", "bio_mention", "personal_channel" }, SignsOf(rows, newcomer));
            Assert.AreEqual(0, SignsOf(rows, friend).Length, "a link to the group itself is not a sign");
            Assert.AreEqual(1, GetChatCalls(chat, newcomer.Id), "looked up once, on the first message");
            Assert.AreEqual(1, GetChatCalls(chat, -1008881), "the personal channel (also in the bio) once");

            await using var connection = await chat.Database.OpenConnectionAsync();
            var flags = await connection.QuerySingleAsync<string[]>(@"
SELECT p.flags FROM account_profile_flags p JOIN telegram_accounts a ON a.account_id = p.account_id WHERE a.telegram_user_id = @Id",
                new { Id = newcomer.Id });
            CollectionAssert.IsSubsetOf(new[] { "bio", "bio_link", "bio_mention", "personal_channel" }, flags);
            var channel = await connection.QuerySingleAsync<(string Username, int Members)>(
                "SELECT username, member_count FROM channel_profiles WHERE telegram_chat_id = -1008881");
            Assert.AreEqual(("gemsdaily", 100), channel);
            Assert.IsNotNull(chat.Telegram.SentMessages.SingleOrDefault(m => m.Chat.Id == admin.Id && m.Text?.StartsWith("🛡") == true),
                "a link, a mention and a channel add up to a report");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnAdminsFirstPost_IsNotLookedUp()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            chat.Telegram.SetProfile(admin.Id, "https://shop.example");

            await chat.SendAsync(admin, "hello");

            Assert.AreEqual(0, GetChatCalls(chat, admin.Id) + (await Signs(chat)).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ABioRule_ActsWhenTheyJoin_BeforeTheyWriteAnything()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var rule = await LiveRule(chat, admin, "bio", "ban", "casino");
            var spammer = chat.Add("Lucky");
            chat.Telegram.SetProfile(spammer.Id, "Best CASINO bonuses, dm me");
            var other = chat.Add("Other");
            chat.Telegram.SetProfile(other.Id, "I like cats");

            await chat.JoinAsync(spammer);
            await chat.JoinAsync(other);

            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, spammer.Id));
            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, other.Id));
            CollectionAssert.AreEqual(new[] { $"filter:{rule.Id}" }, SignsOf(await Signs(chat), spammer));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task WithoutABioRule_NobodyIsLookedUpOnJoining()
        {
            await using var chat = await HarnessChat.StartAsync();
            var joiner = chat.Add("Joiner");
            chat.Telegram.SetProfile(joiner.Id, "https://shop.example");

            await chat.JoinAsync(joiner);

            Assert.AreEqual(0, GetChatCalls(chat, joiner.Id));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AChannelRule_DeletesAForwardFromAMatchingChannel()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var rule = await LiveRule(chat, admin, "channel", "delete", "signals");
            chat.Telegram.AddChannel(-1007771, "Crypto Signals VIP", description: "daily pumps");
            chat.Telegram.AddChannel(-1007772, "Cute cats");
            var newcomer = chat.Add("Newcomer");
            await chat.SendAsync(newcomer, "hi");
            Message Forward(long channel, string title)
            {
                var message = chat.Text(newcomer, "look");
                message.ForwardOrigin = new MessageOriginChannel { Chat = new Chat { Id = channel, Type = ChatType.Channel, Title = title }, MessageId = 1 };
                return message;
            }

            await chat.PostAsync(Forward(-1007772, "Cute cats"));
            await chat.PostAsync(Forward(-1007771, "Crypto Signals VIP"));

            Assert.IsTrue(chat.Telegram.Calls.Any(c => c.Method == "deleteMessage" && c.Succeeded), "the forward is deleted");
            Assert.AreEqual(1, chat.Telegram.Calls.Count(c => c.Method == "deleteMessage" && c.Succeeded));
            Assert.AreEqual(2, (await Signs(chat, "forward_channel")).Length, "both forwards are signs");
            CollectionAssert.AreEqual(new[] { $"filter:{rule.Id}" }, SignsOf(await Signs(chat), newcomer), "only the matching channel");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AMentionedChannel_IsRunThroughTheChannelRules()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var rule = await LiveRule(chat, admin, "channel", "report", "pump");
            chat.Telegram.AddChannel(-1007773, "Moon", username: "moonchan", description: "100x pump every day");
            var newcomer = chat.Add("Newcomer");
            await chat.SendAsync(newcomer, "hi");

            var message = chat.Text(newcomer, "join @moonchan");
            message.Entities = new[] { new MessageEntity { Type = MessageEntityType.Mention, Offset = 5, Length = 9 } };
            await chat.PostAsync(message);

            CollectionAssert.Contains(SignsOf(await Signs(chat), newcomer), $"filter:{rule.Id}");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AProfile_IsLookedUpOnceADay_ForEveryGroup()
        {
            await using var chat = await HarnessChat.StartAsync();
            var other = chat.Group(-1009999999);
            var newcomer = chat.Add("Newcomer");
            other.Telegram.SetMember(other.ChatId, newcomer.Id, MemberState.Member);
            chat.Telegram.SetProfile(newcomer.Id, "https://shop.example");

            await chat.SendAsync(newcomer, "hello");
            await other.SendAsync(newcomer, "hello");
            Assert.AreEqual(1, GetChatCalls(chat, newcomer.Id));

            var rows = await Signs(chat);
            Assert.AreEqual(2, rows.Count(r => r.User == newcomer.Id && r.Sign == "bio_link"), "a sign in each group");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task DuringARaid_NobodyIsLookedUp()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await chat.SendAsync(admin, "/raid");
            Assert.IsTrue(RaidGuard.Quiet(chat.ChatId));
            var newcomer = chat.Add("Newcomer");
            chat.Telegram.SetProfile(newcomer.Id, "https://shop.example");

            await chat.SendAsync(newcomer, "hello");

            Assert.AreEqual(0, GetChatCalls(chat, newcomer.Id));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task FilterTest_TriesABioRule_OnTheBiosLookedUpToday()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var a = chat.Add("Alpha");
            chat.Telegram.SetProfile(a.Id, "casino king");
            var b = chat.Add("Beta");
            chat.Telegram.SetProfile(b.Id, "gardening");
            await chat.SendAsync(a, "hi");
            await chat.SendAsync(b, "hi");

            await chat.SendAsync(admin, "/filter add bio report casino");
            var rule = (await new PostgresDetectionSettingsRepository(() => chat.Database).LoadAsync(chat.ChatId)).Filters.OwnRules.Single();
            await chat.AdvanceAsync(TimeSpan.FromSeconds(20));
            await chat.SendAsync(admin, $"/filter test {rule.Id}");

            var result = chat.Telegram.LastMessageTo(admin.Id).Text;
            StringAssert.StartsWith(result, "1 of the 2 bios");
            StringAssert.Contains(result, "Alpha");
            Assert.IsFalse(result.Contains("casino king"), "the bio itself is not shown");
            AssertNoErrors(chat);
        }
    }
}
