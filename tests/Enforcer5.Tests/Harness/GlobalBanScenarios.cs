using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Global bans (PostgreSQL rollout step 7), which run on PostgreSQL alone: the commands write
    /// <c>global_sanctions</c>, and the check on every message reads the in-memory set loaded from
    /// it. Needs ENFORCER_TEST_POSTGRES (enf-sql subprod --run -- dotnet test ...).
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class GlobalBanScenarios
    {
        private sealed class Row
        {
            public long TargetTelegramId { get; set; }
            public long? ActorTelegramId { get; set; }
            public string Kind { get; set; }
            public string Status { get; set; }
            public string Reason { get; set; }
            public string ResolutionReason { get; set; }
            public bool Resolved { get; set; }
            public bool Notified { get; set; }
        }

        private static async Task<Row[]> GlobalSanctions(HarnessChat chat)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<Row>(@"
SELECT target.telegram_user_id AS TargetTelegramId, actor.telegram_user_id AS ActorTelegramId, gs.kind AS Kind,
       gs.status AS Status, gs.reason AS Reason, gs.resolution_reason AS ResolutionReason,
       gs.resolved_at IS NOT NULL AS Resolved, gs.notified_at IS NOT NULL AS Notified
FROM global_sanctions gs
JOIN telegram_accounts target ON target.account_id = gs.account_id
LEFT JOIN user_identity_links l ON l.user_id = gs.actor_user_id AND l.unlinked_at IS NULL
LEFT JOIN telegram_accounts actor ON actor.account_id = l.account_id
ORDER BY gs.global_sanction_id")).ToArray();
        }

        private static async Task<string[]> GroupBanReasons(HarnessChat chat)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<string>("SELECT reason FROM sanctions WHERE kind = 'ban' ORDER BY sanction_id")).ToArray();
        }

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static HarnessUser GlobalAdmin(HarnessChat chat) =>
            chat.Add("Global admin", MemberState.Administrator, id: Models.Constants.GlobalAdmins[0]);

        [TestMethod]
        public async Task Rekt_ThenUnrekt()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = GlobalAdmin(chat);
            var spammer = chat.Add("Spammer");

            await chat.SendAsync(admin, $"/getrekt {spammer.Id}:spam network");

            var ban = (await GlobalSanctions(chat)).Single();
            Assert.AreEqual((spammer.Id, (long?)admin.Id, "ban", "active", "spam network", false),
                (ban.TargetTelegramId, ban.ActorTelegramId, ban.Kind, ban.Status, ban.Reason, ban.Resolved));
            Assert.AreEqual("spam network", GlobalBans.Get(spammer.Id)?.Reason);
            Assert.AreEqual($"{spammer.Id} has been rekt for spam network", chat.Replies.Last());

            await chat.SendAsync(admin, $"/unrekt {spammer.Id}:appeal accepted");

            var lifted = (await GlobalSanctions(chat)).Single();
            Assert.AreEqual(("revoked", "appeal accepted", true), (lifted.Status, lifted.ResolutionReason, lifted.Resolved));
            Assert.IsNull(GlobalBans.Get(spammer.Id));
            Assert.AreEqual($"{spammer.Id} has been unrekt for appeal accepted", chat.Replies.Last());
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task RektAgain_ReplacesTheActiveBan()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = GlobalAdmin(chat);
            var spammer = chat.Add("Spammer");

            await chat.SendAsync(admin, $"/getrekt {spammer.Id}:first");
            await chat.SendAsync(admin, $"/getrekt {spammer.Id}:second");

            var rows = await GlobalSanctions(chat);
            CollectionAssert.AreEqual(new[] { "revoked", "active" }, rows.Select(r => r.Status).ToArray());
            Assert.AreEqual(PostgresGlobalBanRepository.Superseded, rows[0].ResolutionReason);
            Assert.AreEqual("second", GlobalBans.Get(spammer.Id)?.Reason);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task RektByIdList()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = GlobalAdmin(chat);

            await chat.SendAsync(admin, "/getrekti 5550001,5550002:raid");

            var rows = await GlobalSanctions(chat);
            CollectionAssert.AreEquivalent(new[] { 5550001L, 5550002L }, rows.Select(r => r.TargetTelegramId).ToArray());
            Assert.IsTrue(rows.All(r => r.Status == "active" && r.Reason == "raid"));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task UnrektWithoutABan_SaysSo()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = GlobalAdmin(chat);

            await chat.SendAsync(admin, "/unrekt 5550003:nothing");

            Assert.AreEqual("5550003 has no global ban", chat.Replies.Last());
            Assert.AreEqual(0, (await GlobalSanctions(chat)).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task RektedMemberJoining_IsBannedFromTheGroup()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = GlobalAdmin(chat);
            var spammer = new HarnessUser(5550004, "Spammer");
            await chat.SendAsync(admin, $"/getrekt {spammer.Id}:scam");

            await chat.JoinAsync(spammer);

            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, spammer.Id));
            CollectionAssert.AreEqual(new[] { "Global ban: scam" }, await GroupBanReasons(chat));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task UnrektedMemberJoining_IsLeftAlone()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = GlobalAdmin(chat);
            var member = new HarnessUser(5550005, "Member");
            await chat.SendAsync(admin, $"/getrekt {member.Id}:mistake");
            await chat.SendAsync(admin, $"/unrekt {member.Id}:sorry");

            await chat.JoinAsync(member);

            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, member.Id));
            Assert.AreEqual(0, (await GroupBanReasons(chat)).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task BanMadeByTheOtherEdition_IsEnforcedAfterAReload()
        {
            await using var chat = await HarnessChat.StartAsync();
            var spammer = chat.Add("Spammer");
            // Written straight to the table, as the other edition's process would.
            await Repositories.GlobalBans.BanAsync(spammer.Id, "elsewhere", Models.Constants.GlobalAdmins[0], DateTimeOffset.UtcNow);

            await chat.SendAsync(spammer, "hello");
            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, spammer.Id), "not loaded yet");

            await GlobalBans.ReloadAsync();
            await chat.SendAsync(spammer, "hello again");

            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, spammer.Id));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task SupportChat_IsToldOncePerBan()
        {
            await using var chat = await HarnessChat.StartAsync(chatId: Models.Constants.SupportId, title: "Support");
            var admin = GlobalAdmin(chat);
            var banned = chat.Add("Banned");
            await chat.SendAsync(admin, $"/getrekt {banned.Id}:spam");

            await chat.SendAsync(banned, "why am I banned");
            await chat.SendAsync(banned, "hello?");

            Assert.AreEqual(1, chat.Replies.Count(r => r.Contains("has a global ban record")));
            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, banned.Id), "never banned from support");
            Assert.IsTrue((await GlobalSanctions(chat)).Single().Notified);

            // A new ban is told again.
            await chat.SendAsync(admin, $"/getrekt {banned.Id}:spam again");
            await chat.SendAsync(banned, "still here");
            Assert.AreEqual(2, chat.Replies.Count(r => r.Contains("has a global ban record")));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Rektlist_ListsActiveBans()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = GlobalAdmin(chat);
            await chat.SendAsync(admin, "/getrekti 5550006,5550007:raid");
            await chat.SendAsync(admin, "/unrekt 5550007:fine");

            await chat.SendAsync(admin, "/rektlist");

            var reply = chat.Replies.Last();
            StringAssert.StartsWith(reply, "1 active global bans:");
            StringAssert.Contains(reply, "5550006: raid");
            Assert.IsFalse(reply.Contains("5550007"));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task PostgresNotReady_ChangesNothing()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = GlobalAdmin(chat);
            PostgresStorage.UseForTests(null);
            try
            {
                await chat.SendAsync(admin, "/getrekt 5550008:spam");
            }
            finally
            {
                PostgresStorage.UseForTests(chat.Database);
            }

            StringAssert.Contains(chat.Replies.Last(), "not ready");
            Assert.AreEqual(0, (await GlobalSanctions(chat)).Length);
            Assert.IsNull(GlobalBans.Get(5550008));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ARektedMemberOnTheWatchList_MayStay()
        {
            // By design: /disablewatch lets a group keep someone it vouches for.
            await using var chat = await HarnessChat.StartAsync();
            var admin = GlobalAdmin(chat);
            var vouched = chat.Add("Vouched");
            await chat.SendAsync(admin, $"/getrekt {vouched.Id}:spam");
            chat.Keys.AddToSet($"chat:{chat.ChatId}:watch", vouched.Id.ToString());

            await chat.SendAsync(vouched, "hello");

            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, vouched.Id));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AWatchedMemberAddingARektedAccount_DoesNotBringItIn()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = GlobalAdmin(chat);
            var watched = chat.Add("Watched");
            chat.Keys.AddToSet($"chat:{chat.ChatId}:watch", watched.Id.ToString());
            var spammer = new HarnessUser(5550005, "Spammer");
            await chat.SendAsync(admin, $"/getrekt {spammer.Id}:scam");

            await chat.PostAsync(chat.Join(spammer, addedBy: watched));

            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, spammer.Id), "the joiner's own entry counts, not the adder's");
            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, watched.Id));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ARektedAccountPutOnTheWatchListByIdFirst_MayBeAdded()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = GlobalAdmin(chat);
            var member = chat.Add("Member");
            var vouched = new HarnessUser(5550006, "Vouched");
            await chat.SendAsync(admin, $"/getrekt {vouched.Id}:old ban");
            chat.Keys.AddToSet($"chat:{chat.ChatId}:watch", vouched.Id.ToString());

            await chat.PostAsync(chat.Join(vouched, addedBy: member));

            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, vouched.Id));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ARektedAccountAddedSecond_IsBannedToo()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = GlobalAdmin(chat);
            var adder = chat.Add("Adder");
            var normal = chat.Add("Normal", MemberState.Left);
            var spammer = new HarnessUser(5550007, "Spammer");
            await chat.SendAsync(admin, $"/getrekt {spammer.Id}:scam");
            var join = chat.Join(normal, addedBy: adder);
            chat.Telegram.SetMember(chat.ChatId, spammer.Id, MemberState.Member);
            join.NewChatMembers = new[] { normal.ToUser(), spammer.ToUser() };

            await chat.PostAsync(join);

            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, spammer.Id), "only the first one added was looked up");
            Assert.AreEqual(MemberState.Member, chat.Telegram.GetMember(chat.ChatId, normal.Id));
            CollectionAssert.AreEqual(new[] { "Global ban: scam" }, await GroupBanReasons(chat));
            AssertNoErrors(chat);
        }
    }
}
