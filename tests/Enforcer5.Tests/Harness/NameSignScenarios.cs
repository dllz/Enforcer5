using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Detection step 10: names that copy an admin or a bot of the group, and names that claim to be staff. Needs
    /// the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Detection")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class NameSignScenarios
    {
        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static async Task<(long User, string Sign, string Mode, string Evidence)[]> Signs(HarnessChat chat)
        {
            await DetectionRecorder.FlushAsync();
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<(long, string, string, string)>(@"
SELECT a.telegram_user_id, d.sign, d.mode, d.evidence::text FROM detections d JOIN telegram_accounts a ON a.account_id = d.account_id
WHERE d.sign IN ('name_impersonation', 'name_keyword') ORDER BY d.detection_id")).ToArray();
        }

        private static string[] SignsOf((long User, string Sign, string Mode, string Evidence)[] rows, HarnessUser user) =>
            rows.Where(r => r.User == user.Id).Select(r => r.Sign).OrderBy(s => s).ToArray();

        private static Task GoLive(HarnessChat chat) =>
            chat.ChangeSettingsAsync(p => p with { Engine = p.Engine with { ObserveUntil = chat.Clock.GetUtcNow().AddMinutes(-1) } });

        private static Telegram.Bot.Types.Message ReportTo(HarnessChat chat, HarnessUser admin) =>
            chat.Telegram.SentMessages.SingleOrDefault(m => m.Chat.Id == admin.Id && m.Text?.StartsWith("🛡") == true);

        [TestMethod]
        public async Task ANewcomerNamedLikeAnAdmin_IsReportedOnJoining_NamingTheAdmin()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            var admin = chat.Add("Daniel", MemberState.Administrator, username: "danieladmin", lastName: "Kayo");
            var copy = chat.Add("Dаniel", lastName: "Kayo"); // a Cyrillic а

            await chat.JoinAsync(copy);

            var rows = await Signs(chat);
            CollectionAssert.AreEqual(new[] { "name_impersonation" }, SignsOf(rows, copy));
            Assert.AreEqual("live", rows.Single().Mode);
            StringAssert.Contains(rows.Single().Evidence, $"\"of\": {admin.Id}");
            var report = ReportTo(chat, admin);
            Assert.IsNotNull(report, "the admins hear of it at once");
            StringAssert.Contains(report.Text, "name like an admin's or a bot's (Daniel Kayo (@danieladmin))");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnAdminsNameWithSupport_IsBothSigns_AndAStaffWordAloneOnlyScores()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            var admin = chat.Add("Daniel", MemberState.Administrator);
            var support = chat.Add("Daniel | Support");
            var official = chat.Add("Official Help Desk");

            await chat.JoinAsync(support);
            await chat.JoinAsync(official);

            var rows = await Signs(chat);
            CollectionAssert.AreEqual(new[] { "name_impersonation", "name_keyword" }, SignsOf(rows, support));
            CollectionAssert.AreEqual(new[] { "name_keyword" }, SignsOf(rows, official));
            var report = ReportTo(chat, admin);
            Assert.IsNotNull(report);
            Assert.IsFalse(report.Text.Contains("Official Help Desk"), "a staff word alone (2) stays under the report level (3)");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ANameChangedAfterJoining_IsSeenOnTheNextMessage_Once()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var newcomer = chat.Add("Newcomer");
            await chat.JoinAsync(newcomer);
            await chat.SendAsync(newcomer, "hi all");
            Assert.AreEqual(0, (await Signs(chat)).Length);

            // Renamed to the bot itself.
            var renamed = newcomer with { FirstName = HarnessChat.BotUser.FirstName };
            await chat.SendAsync(renamed, "please send me your login code");
            await chat.SendAsync(renamed, "quickly");
            await chat.AdvanceAsync(TimeSpan.FromMinutes(1));

            var rows = await Signs(chat);
            CollectionAssert.AreEqual(new[] { "name_impersonation" }, SignsOf(rows, newcomer), "once, not for each message");
            StringAssert.Contains(rows.Single().Evidence, "\"role\": \"bot\"");
            Assert.IsNotNull(ReportTo(chat, admin));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ABotSeenJoining_IsABotOfTheGroup()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var gameBot = chat.Add("Werewolf Moderator", username: "werewolfmodbot", isBot: true);
            await chat.PostAsync(chat.Join(gameBot, addedBy: admin));
            var copy = chat.Add("Werewolf Moderator");

            await chat.JoinAsync(copy);

            CollectionAssert.AreEqual(new[] { "name_impersonation", "name_keyword" }, SignsOf(await Signs(chat), copy));
            Assert.AreEqual(0, SignsOf(await Signs(chat), gameBot).Length, "bots are not checked");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AdminsAndEstablishedMembers_AreLeftAlone()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            chat.Add("Daniel", MemberState.Administrator);
            var coAdmin = chat.Add("Daniel", MemberState.Administrator);
            var regular = chat.Add("Regular");
            chat.Hashes[$"{chat.ChatId}:users:{regular.Id}", "msgs"] = "5000";

            await chat.SendAsync(coAdmin, "hello");
            await chat.SendAsync(regular with { FirstName = "Daniel Support" }, "hello");

            Assert.AreEqual(0, (await Signs(chat)).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task WhileWatching_ItIsRecorded_AndNobodyIsTold()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Daniel", MemberState.Administrator);
            var copy = chat.Add("Daniel");

            await chat.JoinAsync(copy);

            var rows = await Signs(chat);
            Assert.AreEqual("observe", rows.Single().Mode);
            Assert.IsNull(ReportTo(chat, admin));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AJoinSeenAsServiceMessageAndMemberUpdate_CountsOnce()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            chat.Add("Daniel", MemberState.Administrator);
            var copy = chat.Add("Daniel");

            chat.Deliver(chat.Join(copy));
            chat.Deliver(chat.MemberJoined(copy, inviteLink: "https://t.me/+abc"));
            await chat.SettleAsync();

            CollectionAssert.AreEqual(new[] { "name_impersonation" }, SignsOf(await Signs(chat), copy));
            AssertNoErrors(chat);
        }
    }
}
