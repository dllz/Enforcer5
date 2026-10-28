using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>Detection step 12: files admins banned are deleted when posted again. Needs the test database.</summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Detection")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class BannedMediaScenarios
    {
        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static bool Present(HarnessChat chat, Telegram.Bot.Types.Message message) =>
            chat.Telegram.MessagesFrom(chat.ChatId, message.From.Id).Contains(message.MessageId);

        private static async Task<string[]> Stored(HarnessChat chat)
        {
            await chat.SettleAsync();
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<string>("SELECT media_unique_id || ':' || source FROM banned_media ORDER BY added_at")).ToArray();
        }

        private static async Task<int> Signs(HarnessChat chat)
        {
            await DetectionRecorder.FlushAsync();
            await using var connection = await chat.Database.OpenConnectionAsync();
            return await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM detections WHERE sign = 'banned_media'");
        }

        private static HarnessUser Established(HarnessChat chat, string name)
        {
            var user = chat.Add(name);
            chat.Hashes[$"{chat.ChatId}:users:{user.Id}", "msgs"] = "5000";
            return user;
        }

        [TestMethod]
        public async Task ABanReplyingToAPhoto_BansThePhoto_ForEveryoneButAdmins_AlsoWhileWatching()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var spammer = chat.Add("Spammer");
            var spam = await chat.PostAsync(chat.Photo(spammer, uniqueId: "scam-image"));
            await chat.SendAsync(admin, "/ban", replyTo: spam);
            CollectionAssert.AreEqual(new[] { "scam-image:ban" }, await Stored(chat));

            var newcomer = chat.Add("Newcomer");
            var regular = Established(chat, "Regular");
            var again = await chat.PostAsync(chat.Photo(newcomer, uniqueId: "scam-image"));
            var shared = await chat.PostAsync(chat.Photo(regular, uniqueId: "scam-image"));
            var adminsCopy = await chat.PostAsync(chat.Photo(admin, uniqueId: "scam-image"));
            var other = await chat.PostAsync(chat.Photo(newcomer, uniqueId: "holiday"));

            Assert.IsFalse(Present(chat, again), "deleted, although the group only watches");
            Assert.IsFalse(Present(chat, shared), "established members too");
            Assert.IsTrue(Present(chat, adminsCopy), "admins' posts stay");
            Assert.IsTrue(Present(chat, other));
            Assert.AreEqual(2, await Signs(chat));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task BanMedia_BansTheRepliedFile_AndUnbanMediaLiftsIt()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var first = await chat.PostAsync(chat.Photo(member, uniqueId: "meme"));

            await chat.SendAsync(admin, "/banmedia", replyTo: first);
            Assert.IsFalse(Present(chat, first), "the post replied to goes");
            Assert.AreEqual(Reply(chat, "banMediaAdded"), chat.Replies.Last());

            var second = await chat.PostAsync(chat.Photo(member, uniqueId: "meme"));
            Assert.IsFalse(Present(chat, second));

            var adminsCopy = await chat.PostAsync(chat.Photo(admin, uniqueId: "meme"));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(20)); // the command limiter
            await chat.SendAsync(admin, "/banmedia");
            StringAssert.StartsWith(chat.Replies.Last(), Reply(chat, "banMediaCount", 1));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(20));
            await chat.SendAsync(admin, "/unbanmedia", replyTo: adminsCopy);
            Assert.AreEqual(Reply(chat, "banMediaRemoved"), chat.Replies.Last());
            Assert.AreEqual(0, (await Stored(chat)).Length);

            var third = await chat.PostAsync(chat.Photo(member, uniqueId: "meme"));
            Assert.IsTrue(Present(chat, third));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task UnbanMediaAll_LiftsEveryBan_AndATextReplyIsRefused()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var text = await chat.SendAsync(member, "just text");
            await chat.SendAsync(admin, "/banmedia", replyTo: text);
            Assert.AreEqual(Reply(chat, "banMediaNoFile"), chat.Replies.Last());
            Assert.IsTrue(Present(chat, text));

            foreach (var id in new[] { "a", "b" })
            {
                await chat.AdvanceAsync(TimeSpan.FromSeconds(20));
                await chat.SendAsync(admin, "/banmedia", replyTo: await chat.PostAsync(chat.Photo(member, uniqueId: id)));
            }
            await chat.AdvanceAsync(TimeSpan.FromSeconds(20));
            await chat.SendAsync(admin, "/unbanmedia all");
            Assert.AreEqual(Reply(chat, "banMediaCleared", 2), chat.Replies.Last());
            Assert.AreEqual(0, (await Stored(chat)).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheBans_SurviveARestart()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            await chat.SendAsync(admin, "/banmedia", replyTo: await chat.PostAsync(chat.Photo(member, uniqueId: "gore")));
            await Stored(chat);

            await chat.CrashAndRestartAsync();
            var again = await chat.PostAsync(chat.Photo(member, uniqueId: "gore"));
            await chat.SettleAsync();

            Assert.IsFalse(Present(chat, again), "read back from PostgreSQL");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnEditIntoABannedFile_IsDeleted()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = Established(chat, "Member");
            await chat.SendAsync(admin, "/banmedia", replyTo: await chat.PostAsync(chat.Photo(chat.Add("Spammer"), uniqueId: "nsfw")));
            var post = await chat.PostAsync(chat.Photo(member, uniqueId: "cat"));
            await chat.AdvanceAsync(TimeSpan.FromMinutes(2));

            await chat.EditAsync(post, chat.Photo(member, uniqueId: "nsfw"));

            Assert.IsFalse(Present(chat, post));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task BanOnAReport_BansTheFileOfTheReportedPost()
        {
            await using var chat = await HarnessChat.StartAsync();
            await chat.ChangeSettingsAsync(p => p with { Engine = p.Engine with { ObserveUntil = chat.Clock.GetUtcNow().AddMinutes(-1) } });
            var admin = chat.Add("Admin", MemberState.Administrator);
            var newcomer = chat.Add("Newcomer");
            await chat.SendAsync(newcomer, "hi");
            var post = await chat.PostAsync(chat.Photo(newcomer, uniqueId: "pump"));
            await DetectionReports.ReportAsync(chat.ChatId, newcomer.Id, post.MessageId, SignCatalog.LinkOutside.Name, null, 3);
            await chat.SettleAsync();
            var report = chat.Telegram.SentMessages.Last(m => m.Chat.Id == admin.Id && m.Text?.StartsWith("🛡") == true);

            await chat.PressAsync(admin, report, $"{DetectionReports.Trigger}:");
            await chat.SettleAsync();

            CollectionAssert.AreEqual(new[] { "pump:report" }, await Stored(chat));
            var repost = await chat.PostAsync(chat.Photo(chat.Add("Another"), uniqueId: "pump"));
            Assert.IsFalse(Present(chat, repost));
            AssertNoErrors(chat);
        }

        private static string Reply(HarnessChat chat, string key, params object[] args) =>
            Enforcer5.Helpers.Methods.GetLocaleString(Enforcer5.Helpers.Methods.GetGroupLanguage(chat.ChatId).Doc, key, args);
    }
}
