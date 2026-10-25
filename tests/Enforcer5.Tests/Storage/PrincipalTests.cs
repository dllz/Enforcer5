using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StackExchange.Redis;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.Storage
{
    [TestClass]
    public class ObservationBufferTests
    {
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

        private static Message GroupMessage(User from, DateTimeOffset at, string title = "Group", string username = null) => new Message
        {
            Id = 1,
            Date = at.UtcDateTime,
            From = from,
            Chat = new Chat { Id = -1001, Type = ChatType.Supergroup, Title = title, Username = username },
            Text = "hi"
        };

        [TestMethod]
        public void Message_RecordsSenderGroupAndRepliedUser()
        {
            var buffer = new ObservationBuffer();
            var message = GroupMessage(new User { Id = 1, FirstName = "Ann", LastName = "Lee", Username = "ann", LanguageCode = "nl" }, T0,
                username: "grp");
            message.ReplyToMessage = new Message { From = new User { Id = 2, FirstName = "Bob" } };
            buffer.Collect(new Update { Message = message });

            var (batch, dropped) = buffer.Take();
            Assert.AreEqual(0, dropped);
            var ann = batch.Accounts.Single(a => a.TelegramUserId == 1);
            Assert.AreEqual(("Ann Lee", "ann", "nl", (DateTimeOffset?)T0), (ann.DisplayName, ann.Username, ann.LanguageCode, ann.LastSeenAt));
            var bob = batch.Accounts.Single(a => a.TelegramUserId == 2);
            Assert.IsNull(bob.LastSeenAt, "being replied to is not activity");
            var chat = batch.Chats.Single();
            Assert.AreEqual(("supergroup", "Group", "grp", (DateTimeOffset?)T0, (bool?)true),
                (chat.ChatType, chat.Title, chat.Username, chat.LastMessageAt, chat.Active));
            Assert.IsTrue(buffer.Take().Batch.IsEmpty, "Take empties the buffer");
        }

        [TestMethod]
        public void Merge_NewestProfile_LatestSeen_KeepsKnownLanguage()
        {
            var buffer = new ObservationBuffer();
            buffer.Add(new AccountObservation(1, false, "Ann", "ann", "nl", T0.AddMinutes(5)));
            buffer.Add(new AccountObservation(1, false, "Ann B", null, null, T0));
            var ann = buffer.Take().Batch.Accounts.Single();
            Assert.AreEqual(("Ann B", (string)null, "nl", (DateTimeOffset?)T0.AddMinutes(5)),
                (ann.DisplayName, ann.Username, ann.LanguageCode, ann.LastSeenAt));
        }

        [TestMethod]
        public void PlaceholderSenders_AreNotPeople()
        {
            var buffer = new ObservationBuffer();
            var message = GroupMessage(new User { Id = 136817688, FirstName = "Channel" }, T0);
            message.SenderChat = new Chat { Id = -1009, Type = ChatType.Channel, Title = "News", Username = "news" };
            buffer.Collect(new Update { Message = message });
            var batch = buffer.Take().Batch;
            Assert.AreEqual(0, batch.Accounts.Count);
            var channel = batch.Chats.Single(c => c.TelegramChatId == -1009);
            Assert.AreEqual(("channel", "News", (bool?)null), (channel.ChatType, channel.Title, channel.Active));
        }

        [TestMethod]
        public void BotLeaving_MarksTheGroupInactive()
        {
            var me = Bot.Me;
            Bot.Me = new User { Id = 99, IsBot = true, FirstName = "Enforcer" };
            try
            {
                var buffer = new ObservationBuffer();
                var message = GroupMessage(new User { Id = 1, FirstName = "Admin" }, T0);
                message.LeftChatMember = Bot.Me;
                buffer.Collect(new Update { Message = message });
                Assert.AreEqual(false, buffer.Take().Batch.Chats.Single().Active);
            }
            finally
            {
                Bot.Me = me;
            }
        }

        [TestMethod]
        public void Caps_DropAndCount()
        {
            var buffer = new ObservationBuffer(maxAccounts: 2, maxChats: 1);
            for (var i = 1; i <= 4; i++) buffer.Add(new AccountObservation(i, false, "x", null, null, T0));
            buffer.Add(new AccountObservation(1, false, "still merged", null, null, T0));
            var (batch, dropped) = buffer.Take();
            Assert.AreEqual(2, batch.Accounts.Count);
            Assert.AreEqual(2, dropped);
            Assert.AreEqual("still merged", batch.Accounts.Single(a => a.TelegramUserId == 1).DisplayName);
        }

        [TestMethod]
        public void Restore_PutsAFailedBatchUnderNewerObservations()
        {
            var buffer = new ObservationBuffer();
            buffer.Add(new AccountObservation(1, false, "Old", "old", "nl", T0));
            var failed = buffer.Take().Batch;
            buffer.Add(new AccountObservation(1, false, "New", "new", null, null));
            buffer.Restore(failed);
            var ann = buffer.Take().Batch.Accounts.Single();
            Assert.AreEqual(("New", "new", "nl", (DateTimeOffset?)T0), (ann.DisplayName, ann.Username, ann.LanguageCode, ann.LastSeenAt));
        }

        [DataTestMethod]
        [DataRow("grp", "https://t.me/x", "https://t.me/grp")]
        [DataRow(null, "https://t.me/+abc", "https://t.me/+abc")]
        [DataRow(null, "no", null)]
        [DataRow(null, null, null)]
        public void PublicLink_PrefersTheUsername(string username, string stored, string expected)
        {
            Assert.AreEqual(expected, PrincipalObserver.PublicLink(username, stored == null ? RedisValue.Null : stored));
        }
    }

    /// <summary>The batch SQL against a real PostgreSQL; skipped without ENFORCER_TEST_POSTGRES.</summary>
    [TestClass]
    public class PostgresPrincipalRepositoryTests
    {
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);
        private string _connectionString;
        private PostgresDatabase _database;
        private PostgresPrincipalRepository _repo;

        [TestInitialize]
        public async Task Setup()
        {
            _connectionString = await TestPostgres.CreateSchemaAsync();
            await SchemaMigrationRunner.ApplyAsync(_connectionString);
            _database = new PostgresDatabase(_connectionString);
            _repo = new PostgresPrincipalRepository(() => _database);
        }

        [TestCleanup]
        public async Task Cleanup()
        {
            if (_database != null) await _database.DisposeAsync();
            if (_connectionString != null) await TestPostgres.DropSchemaAsync(_connectionString);
        }

        private Task Apply(DateTimeOffset now, AccountObservation[] accounts = null, ChatObservation[] chats = null) =>
            _repo.ApplyAsync(new ObservationBatch(accounts ?? Array.Empty<AccountObservation>(),
                chats ?? Array.Empty<ChatObservation>()), now);

        private async Task<T> One<T>(string sql, object args = null)
        {
            await using var connection = await _database.OpenConnectionAsync();
            return await connection.QuerySingleAsync<T>(sql, args);
        }

        private sealed class Profile
        {
            public string DisplayName { get; set; }
            public string Username { get; set; }
            public string LanguageCode { get; set; }
            public bool Open { get; set; }
        }

        private async Task<Profile[]> Profiles(long telegramUserId)
        {
            await using var connection = await _database.OpenConnectionAsync();
            return (await connection.QueryAsync<Profile>(@"
SELECT p.display_name AS DisplayName, p.username AS Username, p.language_code AS LanguageCode, p.valid_to IS NULL AS Open
FROM telegram_account_profiles p JOIN telegram_accounts a USING (account_id)
WHERE a.telegram_user_id = @telegramUserId ORDER BY p.valid_from", new { telegramUserId })).ToArray();
        }

        [TestMethod]
        public async Task NewAccounts_GetAUserLinkAndProfile()
        {
            await Apply(T0, new[]
            {
                new AccountObservation(1, false, "Ann", "ann", "nl", T0),
                new AccountObservation(2, true, "SomeBot", "some_bot", null, null)
            });
            Assert.AreEqual(2L, await One<long>("SELECT count(*) FROM users"));
            Assert.AreEqual(2L, await One<long>("SELECT count(*) FROM user_identity_links WHERE unlinked_at IS NULL"));
            Assert.IsTrue(await One<bool>("SELECT is_bot FROM telegram_accounts WHERE telegram_user_id = 2"));
            var ann = (await Profiles(1)).Single();
            Assert.AreEqual(("Ann", "ann", "nl", true), (ann.DisplayName, ann.Username, ann.LanguageCode, ann.Open));
        }

        [TestMethod]
        public async Task Profiles_AreHistory_AndUnchangedAddsNothing()
        {
            await Apply(T0, new[] { new AccountObservation(1, false, "Ann", "ann", "nl", T0) });
            await Apply(T0.AddMinutes(1), new[] { new AccountObservation(1, false, "Ann", "ANN", null, T0.AddMinutes(1)) });
            Assert.AreEqual(1, (await Profiles(1)).Length, "same name, username differing only in case, no language: unchanged");

            await Apply(T0.AddMinutes(2), new[] { new AccountObservation(1, false, "Ann Lee", null, null, T0.AddMinutes(2)) });
            var profiles = await Profiles(1);
            Assert.AreEqual(2, profiles.Length);
            Assert.IsFalse(profiles[0].Open);
            Assert.AreEqual(("Ann Lee", (string)null, "nl", true),
                (profiles[1].DisplayName, profiles[1].Username, profiles[1].LanguageCode, profiles[1].Open));
            Assert.AreEqual(1L, await One<long>("SELECT count(*) FROM users"), "one user throughout");
        }

        [TestMethod]
        public async Task LastSeen_OnlyMovesForward()
        {
            await Apply(T0, new[] { new AccountObservation(1, false, "Ann", null, null, T0.AddHours(1)) });
            await Apply(T0, new[] { new AccountObservation(1, false, "Ann", null, null, T0) });
            await Apply(T0, new[] { new AccountObservation(1, false, "Ann", null, null, null) });
            Assert.AreEqual(T0.AddHours(1), await One<DateTimeOffset>("SELECT last_seen_at FROM telegram_accounts"));
        }

        [TestMethod]
        public async Task AccountFromTheBanHistory_GetsItsProfile_NotASecondUser()
        {
            var bans = new PostgresBanHistoryRepository(() => _database);
            await bans.RecordBanAsync(new BanEvent(-1001, 1, BanKind.Ban, null, null, T0, null, "normal"));
            await Apply(T0, new[] { new AccountObservation(1, false, "Ann", "ann", null, T0) },
                new[] { new ChatObservation(-1001, "supergroup", "Group", null, T0, true) });
            Assert.AreEqual(1L, await One<long>("SELECT count(*) FROM users"));
            Assert.AreEqual(1L, await One<long>("SELECT count(*) FROM groups"));
            Assert.AreEqual("Ann", (await Profiles(1)).Single().DisplayName);
            Assert.AreEqual("Group", await One<string>("SELECT title FROM groups"));
        }

        [TestMethod]
        public async Task Groups_TitleLinkCountsAndHistory()
        {
            await Apply(T0, chats: new[]
            {
                new ChatObservation(-1001, "supergroup", "Group", "grp", T0, true, 12, true, "https://t.me/grp")
            });
            await Apply(T0.AddMinutes(1), chats: new[]
            {
                // Redis unreadable this time: count and link keep what is stored.
                new ChatObservation(-1001, "supergroup", "Group renamed", "grp", T0.AddMinutes(1), true)
            });
            await Apply(T0.AddMinutes(2), chats: new[]
            {
                new ChatObservation(-1001, "supergroup", "Group renamed", "grp", T0.AddSeconds(30), false, 14, true, null)
            });

            await using var connection = await _database.OpenConnectionAsync();
            var g = await connection.QuerySingleAsync(@"
SELECT title, username, public_link, active, observed_user_count, last_message_at FROM groups");
            Assert.AreEqual("Group renamed", (string)g.title);
            Assert.AreEqual("grp", (string)g.username);
            Assert.IsNull((string)g.public_link);
            Assert.IsFalse((bool)g.active);
            Assert.AreEqual(14L, (long)g.observed_user_count);
            Assert.AreEqual(T0.AddMinutes(1).UtcDateTime, ((DateTime)g.last_message_at).ToUniversalTime());
            Assert.AreEqual(2L, await One<long>("SELECT count(*) FROM telegram_chat_profiles"));
            Assert.AreEqual(1L, await One<long>("SELECT count(*) FROM telegram_chat_profiles WHERE valid_to IS NULL"));
        }

        [TestMethod]
        public async Task ChannelsAndPrivateChats_AreChatsNotGroups()
        {
            await Apply(T0, chats: new[]
            {
                new ChatObservation(-1009, "channel", "News", "news", T0, null),
                new ChatObservation(5, "private", null, null, T0, null)
            });
            Assert.AreEqual(2L, await One<long>("SELECT count(*) FROM telegram_chats"));
            Assert.AreEqual(0L, await One<long>("SELECT count(*) FROM groups"));
            Assert.AreEqual("News", await One<string>("SELECT title FROM telegram_chat_profiles"));
        }
    }
}
