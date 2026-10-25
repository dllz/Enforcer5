using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Kicks and mutes in the sanction history (PostgreSQL rollout step 5), driven through real
    /// commands and joins in a simulated group. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class KickAndMuteScenarios
    {
        private sealed class Row
        {
            public string Kind { get; set; }
            public string Status { get; set; }
            public string Reason { get; set; }
            public long? ActorTelegramId { get; set; }
            public long TargetTelegramId { get; set; }
            public bool HasExpiry { get; set; }
            public bool Resolved { get; set; }
        }

        private static async Task<Row[]> Sanctions(HarnessChat chat)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<Row>(@"
SELECT s.kind AS Kind, s.status AS Status, s.reason AS Reason, actor.telegram_user_id AS ActorTelegramId,
       target.telegram_user_id AS TargetTelegramId, s.expires_at IS NOT NULL AS HasExpiry,
       s.resolved_at IS NOT NULL AS Resolved
FROM sanctions s
JOIN telegram_accounts target ON target.account_id = s.account_id
LEFT JOIN user_identity_links l ON l.user_id = s.actor_user_id AND l.unlinked_at IS NULL
LEFT JOIN telegram_accounts actor ON actor.account_id = l.account_id
ORDER BY s.sanction_id")).ToArray();
        }

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        [TestMethod]
        public async Task Kick_WithAReason_IsClosedAtOnce()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "rude");

            await chat.SendAsync(admin, "/kick being rude", replyTo: message);

            Assert.AreEqual(MemberState.Left, chat.Telegram.GetMember(chat.ChatId, member.Id));
            var kick = (await Sanctions(chat)).Single();
            Assert.AreEqual(("kick", "completed", "being rude", (long?)admin.Id, member.Id, true),
                (kick.Kind, kick.Status, kick.Reason, kick.ActorTelegramId, kick.TargetTelegramId, kick.Resolved));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task KickMe_IsNotRecorded()
        {
            await using var chat = await HarnessChat.StartAsync();
            var member = chat.Add("Member");

            await chat.SendAsync(member, "/kickme");

            Assert.AreEqual(MemberState.Left, chat.Telegram.GetMember(chat.ChatId, member.Id));
            Assert.AreEqual(0, (await Sanctions(chat)).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Mute_Tempmute_Unmute()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "SHOUTING");

            await chat.SendAsync(admin, "/mute caps", replyTo: message);
            await chat.SendAsync(admin, "/tempmute 2h", replyTo: message);
            var rows = await Sanctions(chat);
            Assert.AreEqual(("mute", "revoked", "caps", (long?)admin.Id), (rows[0].Kind, rows[0].Status, rows[0].Reason, rows[0].ActorTelegramId));
            Assert.AreEqual(("tempmute", "active", "SHOUTING", true), (rows[1].Kind, rows[1].Status, rows[1].Reason, rows[1].HasExpiry));

            await chat.SendAsync(admin, "/unmute", replyTo: message);

            CollectionAssert.AreEqual(new[] { "revoked", "revoked" }, (await Sanctions(chat)).Select(r => r.Status).ToArray());
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task MuteOnJoin_IsRecordedAsEnforcers()
        {
            await using var chat = await HarnessChat.StartAsync();
            // Inverted in the settings: "no" means joiners are muted.
            chat.Hashes[$"chat:{chat.ChatId}:settings", "MuteOnJoin"] = "no";
            var joiner = new HarnessUser(424242, "Joiner");

            await chat.JoinAsync(joiner);

            var mute = (await Sanctions(chat)).Single();
            Assert.AreEqual(("mute", "active", "Mute On Join", (long?)null, joiner.Id),
                (mute.Kind, mute.Status, mute.Reason, mute.ActorTelegramId, mute.TargetTelegramId));
        }

        [TestMethod]
        public async Task MuteRefusedByTelegram_RecordsNothing()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "hello");
            chat.Telegram.SetBotCanRestrict(chat.ChatId, false);

            await chat.SendAsync(admin, "/mute", replyTo: message);

            Assert.AreEqual(0, (await Sanctions(chat)).Length);
        }
    }
}
