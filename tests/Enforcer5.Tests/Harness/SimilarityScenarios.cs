using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>Detection step 13: similarity values, near misses and /detectstats. Needs the test database.</summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Detection")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class SimilarityScenarios
    {
        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static async Task<(long User, string Sign, float Similarity, float Threshold)[]> NearMisses(HarnessChat chat)
        {
            await DetectionRecorder.FlushAsync();
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<(long, string, float, float)>(@"
SELECT a.telegram_user_id, n.sign, n.similarity, n.threshold FROM detection_near_misses n
JOIN telegram_accounts a ON a.account_id = n.account_id ORDER BY n.near_miss_id")).ToArray();
        }

        [TestMethod]
        public async Task ARewordedPost_UnderTheThreshold_IsANearMiss_AndTheSamePost_IsASignWithItsSimilarity()
        {
            await using var chat = await HarnessChat.StartAsync();
            var first = chat.Add("First");
            var second = chat.Add("Second");
            var third = chat.Add("Third");

            await chat.SendAsync(first, "Join the best crypto signals channel today, profit guaranteed for everyone");
            await chat.SendAsync(second, "Join the best crypto signals group today, profit guaranteed for everybody");
            await chat.SendAsync(third, "Join the best crypto signals channel today, profit guaranteed for everyone!!");

            var near = await NearMisses(chat);
            var miss = near.Single(n => n.User == second.Id);
            Assert.AreEqual("same_as_newcomers", miss.Sign);
            Assert.IsTrue(miss.Similarity is > 0.6f and < 0.7f, $"{miss.Similarity}");
            Assert.AreEqual(0.7f, miss.Threshold, 1e-6);

            await using var connection = await chat.Database.OpenConnectionAsync();
            var similarities = (await connection.QueryAsync<float?>(
                "SELECT similarity FROM detections WHERE sign = 'same_as_newcomers' ORDER BY detection_id")).ToArray();
            Assert.AreEqual(1f, similarities.First(), "the third posted the first's text");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ANameCloseToAnAdmins_IsANearMiss_OnceADay_AndOnlyForNewMembers()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Christopher", Enforcer5.Tests.Harness.MemberState.Administrator);
            var newcomer = chat.Add("Christophe Rrr");
            var regular = chat.Add("Christophe Rss");
            chat.Hashes[$"{chat.ChatId}:users:{regular.Id}", "msgs"] = "5000";

            await chat.SendAsync(admin, "hi");
            await chat.SendAsync(newcomer, "hello");
            await chat.SendAsync(newcomer, "hello again");
            await chat.SendAsync(regular, "hello");

            var near = await NearMisses(chat);
            Assert.AreEqual(1, near.Length, string.Join(", ", near));
            Assert.AreEqual((newcomer.Id, "name_impersonation"), (near[0].User, near[0].Sign));
            await using var connection = await chat.Database.OpenConnectionAsync();
            Assert.AreEqual(0, await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM detections WHERE sign = 'name_impersonation'"));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheTuningExport_RunsOnTheSchema()
        {
            await using var chat = await HarnessChat.StartAsync();
            var first = chat.Add("First");
            await chat.SendAsync(first, "Join the best crypto signals channel today, profit guaranteed for everyone");
            await chat.SendAsync(chat.Add("Second"), "Join the best crypto signals group today, profit guaranteed for everybody");
            await DetectionRecorder.FlushAsync();

            var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "tools", "tuning", "export.sql"))) dir = dir.Parent;
            Assert.IsNotNull(dir, "tools/tuning/export.sql");
            var sql = await System.IO.File.ReadAllTextAsync(System.IO.Path.Combine(dir.FullName, "tools", "tuning", "export.sql"));
            await using var connection = await chat.Database.OpenConnectionAsync();
            var rows = (await connection.QueryAsync(sql)).ToList();
            Assert.AreEqual(1, rows.Count, "the near miss");
            Assert.AreEqual("near", (string)rows[0].kind);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task DetectStats_SendsTheAdminEachSignsRecord()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", Enforcer5.Tests.Harness.MemberState.Administrator);
            var first = chat.Add("First");
            var second = chat.Add("Second");
            await chat.SendAsync(first, "Join the best crypto signals channel today, profit guaranteed for everyone");
            var spam = await chat.SendAsync(second, "Join the best crypto signals channel today, profit guaranteed for everyone");
            await chat.SendAsync(admin, "/ban", replyTo: spam);
            await DetectionRecorder.FlushAsync();
            await chat.AdvanceAsync(TimeSpan.FromSeconds(20));

            await chat.SendAsync(admin, "/detectstats");

            var stats = chat.Telegram.LastMessageTo(admin.Id).Text;
            StringAssert.Contains(stats, "same message as other new members: 2 times, 2 members (removed 1, muted 0, fine 0)");
            Assert.IsFalse(stats.Contains("crypto"), "no text");
            AssertNoErrors(chat);
        }
    }
}
