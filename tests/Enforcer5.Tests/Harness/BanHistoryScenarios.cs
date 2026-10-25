using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// The ban history (PostgreSQL rollout step 2) driven through real commands in a simulated
    /// group. Needs ENFORCER_TEST_POSTGRES (enf-sql subprod --run -- dotnet test ...).
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class BanHistoryScenarios
    {
        private sealed class Row
        {
            public string Kind { get; set; }
            public string Status { get; set; }
            public string Reason { get; set; }
            public long? ActorTelegramId { get; set; }
            public long TargetTelegramId { get; set; }
            public bool HasExpiry { get; set; }
            public string Edition { get; set; }
        }

        private static async Task<Row[]> Sanctions(HarnessChat chat)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<Row>(@"
SELECT s.kind AS Kind, s.status AS Status, s.reason AS Reason, actor.telegram_user_id AS ActorTelegramId,
       target.telegram_user_id AS TargetTelegramId, s.expires_at IS NOT NULL AS HasExpiry, s.edition AS Edition
FROM sanctions s
JOIN telegram_accounts target ON target.account_id = s.account_id
LEFT JOIN user_identity_links l ON l.user_id = s.actor_user_id AND l.unlinked_at IS NULL
LEFT JOIN telegram_accounts actor ON actor.account_id = l.account_id
ORDER BY s.sanction_id")).ToArray();
        }

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        [TestMethod]
        public async Task Ban_WithAReason_ThenUnban()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var spammer = chat.Add("Spammer");
            var spam = await chat.SendAsync(spammer, "buy now");

            await chat.SendAsync(admin, "/ban spamming links", replyTo: spam);
            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, spammer.Id));
            var ban = (await Sanctions(chat)).Single();
            Assert.AreEqual(("ban", "active", "spamming links", (long?)admin.Id, spammer.Id),
                (ban.Kind, ban.Status, ban.Reason, ban.ActorTelegramId, ban.TargetTelegramId));
            Assert.IsFalse(ban.HasExpiry);

            await chat.SendAsync(admin, $"/unban {spammer.Id}");
            Assert.AreEqual("revoked", (await Sanctions(chat)).Single().Status);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task BanTwice_ThenUnban_ClosesBoth()
        {
            // The flow from the first production test: the second ban supersedes the first.
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var spammer = chat.Add("Spammer");
            var spam = await chat.SendAsync(spammer, "buy now");

            await chat.SendAsync(admin, "/ban", replyTo: spam);
            await chat.SendAsync(admin, "/ban again", replyTo: spam);
            await chat.SendAsync(admin, $"/unban {spammer.Id}");

            var rows = await Sanctions(chat);
            CollectionAssert.AreEqual(new[] { "revoked", "revoked" }, rows.Select(r => r.Status).ToArray());
            Assert.AreEqual("again", rows[1].Reason);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Tempban_HasAnExpiry()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var spammer = chat.Add("Spammer");
            var spam = await chat.SendAsync(spammer, "buy now");

            await chat.SendAsync(admin, "/tempban 2 h", replyTo: spam);

            var ban = (await Sanctions(chat)).Single();
            Assert.AreEqual(("tempban", "active", (long?)admin.Id), (ban.Kind, ban.Status, ban.ActorTelegramId));
            Assert.IsTrue(ban.HasExpiry);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task BanRefusedByTelegram_RecordsNothing()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var other = chat.Add("Other admin", MemberState.Administrator);
            var message = await chat.SendAsync(other, "hello");

            await chat.SendAsync(admin, "/ban", replyTo: message);

            Assert.AreEqual(0, (await Sanctions(chat)).Length);
            Assert.AreEqual(MemberState.Administrator, chat.Telegram.GetMember(chat.ChatId, other.Id));
        }

        [TestMethod]
        public async Task UnbanOfABanFromBeforeTheHistory_WritesNothing()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var old = chat.Add("Banned long ago", MemberState.Kicked);

            await chat.SendAsync(admin, $"/unban {old.Id}");

            Assert.AreEqual(MemberState.Left, chat.Telegram.GetMember(chat.ChatId, old.Id));
            Assert.AreEqual(0, (await Sanctions(chat)).Length);
            AssertNoErrors(chat);
        }
    }
}
