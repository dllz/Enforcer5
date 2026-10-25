using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// The warn history (PostgreSQL rollout step 4) driven through real commands, buttons and media
    /// in a simulated group, checked against both Redis (what the bot reads) and PostgreSQL. Needs
    /// the test database, like every harness scenario.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class WarnHistoryScenarios
    {
        private sealed class Event
        {
            public long Id { get; set; }
            public string Type { get; set; }
            public string Operation { get; set; }
            public long Quantity { get; set; }
            public string Reason { get; set; }
            public long? ActorTelegramId { get; set; }
            public long TargetTelegramId { get; set; }

            public override string ToString() => $"{Type} {Operation} {Quantity}";
        }

        private sealed class Balance
        {
            public string Type { get; set; }
            public long Active { get; set; }
            public long Lifetime { get; set; }
        }

        private sealed class Sanction
        {
            public string Kind { get; set; }
            public string Status { get; set; }
            public string Reason { get; set; }
            public long? TriggerId { get; set; }
        }

        private static async Task<Event[]> Events(HarnessChat chat)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<Event>(@"
SELECT e.warning_event_id AS Id, e.warning_type AS Type, e.operation AS Operation, e.quantity AS Quantity,
       e.reason AS Reason, actor.telegram_user_id AS ActorTelegramId, target.telegram_user_id AS TargetTelegramId
FROM warning_events e
JOIN telegram_accounts target ON target.account_id = e.account_id
LEFT JOIN user_identity_links l ON l.user_id = e.actor_user_id AND l.unlinked_at IS NULL
LEFT JOIN telegram_accounts actor ON actor.account_id = l.account_id
ORDER BY e.warning_event_id")).ToArray();
        }

        private static async Task<Balance> BalanceOf(HarnessChat chat, HarnessUser user, string type)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return await connection.QuerySingleOrDefaultAsync<Balance>(@"
SELECT b.warning_type AS Type, b.active_quantity AS Active, b.lifetime_quantity AS Lifetime
FROM warning_balances b
JOIN user_identity_links l ON l.user_id = b.user_id AND l.unlinked_at IS NULL
JOIN telegram_accounts a ON a.account_id = l.account_id
WHERE a.telegram_user_id = @Id AND b.warning_type = @Type", new { user.Id, Type = type });
        }

        private static async Task<Sanction[]> Sanctions(HarnessChat chat)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<Sanction>(@"
SELECT kind AS Kind, status AS Status, reason AS Reason, trigger_warning_event_id AS TriggerId
FROM sanctions ORDER BY sanction_id")).ToArray();
        }

        private static string[] Ops(Event[] events) => events.Select(e => e.ToString()).ToArray();

        private static long RedisCount(HarnessChat chat, string hash, HarnessUser user) =>
            long.TryParse(chat.Hashes[$"chat:{chat.ChatId}:{hash}", user.Id.ToString()], out var n) ? n : 0;

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static void WarnSettings(HarnessChat chat, int max, string action)
        {
            chat.Hashes[$"chat:{chat.ChatId}:warnsettings", "max"] = max.ToString();
            chat.Hashes[$"chat:{chat.ChatId}:warnsettings", "type"] = action;
        }

        [TestMethod]
        public async Task WarnToMax_WithBan_LinksTheBanToTheLastWarn()
        {
            await using var chat = await HarnessChat.StartAsync();
            WarnSettings(chat, 2, "ban");
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "rude");

            await chat.SendAsync(admin, "/warn", replyTo: message);
            Assert.AreEqual(1, RedisCount(chat, "warns", member));
            var balance = await BalanceOf(chat, member, "standard");
            Assert.AreEqual((1L, 1L), (balance.Active, balance.Lifetime));

            await chat.SendAsync(admin, "/warn", replyTo: message);

            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, member.Id));
            var events = await Events(chat);
            CollectionAssert.AreEqual(new[] { "standard add 1", "standard add 1", "standard reset 2" }, Ops(events));
            Assert.IsTrue(events.All(e => e.TargetTelegramId == member.Id));
            Assert.AreEqual((long?)admin.Id, events[1].ActorTelegramId);
            Assert.AreEqual((null, "Max warns"), (events[2].ActorTelegramId, events[2].Reason));

            var ban = (await Sanctions(chat)).Single();
            Assert.AreEqual(("ban", "active", "Max warns", (long?)events[1].Id), (ban.Kind, ban.Status, ban.Reason, ban.TriggerId));

            Assert.AreEqual(0, RedisCount(chat, "warns", member));
            Assert.AreEqual(2, RedisCount(chat, "totalWarns", member));
            balance = await BalanceOf(chat, member, "standard");
            Assert.AreEqual((0L, 2L), (balance.Active, balance.Lifetime));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task WarnToMax_WithKick_LinksTheKickToTheWarn()
        {
            await using var chat = await HarnessChat.StartAsync();
            WarnSettings(chat, 1, "kick");
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "rude");

            await chat.SendAsync(admin, "/warn", replyTo: message);

            var events = await Events(chat);
            CollectionAssert.AreEqual(new[] { "standard add 1", "standard reset 1" }, Ops(events));
            var kick = (await Sanctions(chat)).Single();
            Assert.AreEqual(("kick", "completed", "Max warns", (long?)events[0].Id), (kick.Kind, kick.Status, kick.Reason, kick.TriggerId));
            Assert.AreEqual(MemberState.Left, chat.Telegram.GetMember(chat.ChatId, member.Id));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Prewarns_ConvertIntoWarns()
        {
            await using var chat = await HarnessChat.StartAsync();
            WarnSettings(chat, 5, "ban");
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "borderline");

            await chat.SendAsync(admin, "/prewarn", replyTo: message);
            await chat.SendAsync(admin, "/prewarn", replyTo: message);
            Assert.AreEqual(2L, (await BalanceOf(chat, member, "prewarning")).Active);

            await chat.SendAsync(admin, "/warn", replyTo: message);

            var events = await Events(chat);
            CollectionAssert.AreEqual(new[]
            {
                "prewarning add 1", "prewarning add 1", "prewarning remove 2", "standard add 3"
            }, Ops(events));
            Assert.AreEqual("Converted into warns", events[2].Reason);
            Assert.IsTrue(events.All(e => e.ActorTelegramId == admin.Id));

            var prewarns = await BalanceOf(chat, member, "prewarning");
            Assert.AreEqual((0L, 2L), (prewarns.Active, prewarns.Lifetime));
            var warns = await BalanceOf(chat, member, "standard");
            Assert.AreEqual((3L, 3L), (warns.Active, warns.Lifetime));
            Assert.AreEqual(3, RedisCount(chat, "warns", member));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task RemoveByButton_AndResetWithAReason()
        {
            await using var chat = await HarnessChat.StartAsync();
            WarnSettings(chat, 5, "ban");
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "rude");

            await chat.SendAsync(admin, "/warn", replyTo: message);
            await chat.SendAsync(admin, "/warn", replyTo: message);
            await chat.PressAsync(admin, chat.LastBotMessage, "removewarn:");
            Assert.AreEqual(1, RedisCount(chat, "warns", member));

            await chat.SendAsync(admin, "/resetwarns said sorry", replyTo: message);

            var events = await Events(chat);
            CollectionAssert.AreEqual(new[]
            {
                "standard add 1", "standard add 1", "standard remove 1", "standard reset 1"
            }, Ops(events));
            Assert.AreEqual((long?)admin.Id, events[2].ActorTelegramId);
            Assert.AreEqual(((long?)admin.Id, "said sorry"), (events[3].ActorTelegramId, events[3].Reason));
            var balance = await BalanceOf(chat, member, "standard");
            Assert.AreEqual((0L, 2L), (balance.Active, balance.Lifetime));
            Assert.AreEqual(0, RedisCount(chat, "warns", member));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task RemovingAWarnNobodyHas_WritesNothing()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "hello");

            await chat.SendAsync(admin, "/removewarn", replyTo: message);
            await chat.SendAsync(admin, "/resetwarns", replyTo: message);

            Assert.AreEqual(0, (await Events(chat)).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task UserMenuWarnButton_ToMax_BansWhenTheGroupSaysBan()
        {
            // The button used to ignore the setting and always kick.
            await using var chat = await HarnessChat.StartAsync();
            WarnSettings(chat, 1, "ban");
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "rude");

            await chat.PressDataAsync(admin, message, $"userbuttonwarnuser:{chat.ChatId}:{member.Id}");

            var events = await Events(chat);
            CollectionAssert.AreEqual(new[] { "standard add 1", "standard reset 1" }, Ops(events));
            Assert.AreEqual((long?)admin.Id, events[0].ActorTelegramId);
            Assert.AreEqual(MemberState.Kicked, chat.Telegram.GetMember(chat.ChatId, member.Id));
            var ban = (await Sanctions(chat)).Single();
            Assert.AreEqual(("ban", "active", (long?)events[0].Id), (ban.Kind, ban.Status, ban.TriggerId));
            // This button never counted towards Redis's totalWarns; the history counts it anyway.
            var balance = await BalanceOf(chat, member, "standard");
            Assert.AreEqual((0L, 1L), (balance.Active, balance.Lifetime));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task MediaWarns_ToTheLimit_Ban()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:media", "image"] = "blocked";
            chat.Hashes[$"chat:{chat.ChatId}:media", "action"] = "ban";
            chat.Hashes[$"chat:{chat.ChatId}:warnsettings", "mediamax"] = "2";
            var member = chat.Add("Member");

            await chat.SendPhotoAsync(member);
            Assert.AreEqual(1L, (await BalanceOf(chat, member, "media")).Active);
            await chat.SendPhotoAsync(member);

            var events = await Events(chat);
            CollectionAssert.AreEqual(new[] { "media add 1", "media add 1", "media reset 2" }, Ops(events));
            Assert.IsTrue(events.All(e => e.ActorTelegramId == null));
            var ban = (await Sanctions(chat)).Single();
            Assert.AreEqual(("Media", (long?)events[1].Id), (ban.Reason, ban.TriggerId));
            var balance = await BalanceOf(chat, member, "media");
            Assert.AreEqual((0L, 2L), (balance.Active, balance.Lifetime));
            Assert.AreEqual(0, RedisCount(chat, "mediawarn", member));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TempbanExpiry_Completes()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "spam");
            await chat.SendAsync(admin, "/tempban 2 h", replyTo: message);
            Assert.AreEqual(1, chat.Hashes.All(HarnessChat.TempbanSchedule).Count);

            await chat.ExpireTempbansAsync();

            Assert.AreEqual(("tempban", "completed"), ((await Sanctions(chat)).Single().Kind, (await Sanctions(chat)).Single().Status));
            Assert.AreEqual(MemberState.Left, chat.Telegram.GetMember(chat.ChatId, member.Id));
            Assert.AreEqual(0, chat.Hashes.All(HarnessChat.TempbanSchedule).Count);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TempbanExpiry_WhenTheUnbanIsRefused_StillCompletes()
        {
            // Telegram lifts the tempban at its until date anyway, and Redis forgets it.
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "spam");
            await chat.SendAsync(admin, "/tempban 2 h", replyTo: message);
            chat.Telegram.SetBotCanRestrict(chat.ChatId, false);

            await chat.ExpireTempbansAsync();

            Assert.AreEqual("completed", (await Sanctions(chat)).Single().Status);
            Assert.AreEqual(0, chat.Hashes.All(HarnessChat.TempbanSchedule).Count);
            Assert.IsTrue(chat.Errors.All(e => e.Contains("Unbanning")), "Errors reported:\n" + string.Join("\n", chat.Errors));
        }
    }
}
