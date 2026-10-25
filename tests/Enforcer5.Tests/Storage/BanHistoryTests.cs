using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Storage
{
    [TestClass]
    public class BanHistoryWriterTests
    {
        private sealed class FakeHistory : IBanHistoryRepository
        {
            public readonly List<string> Calls = new List<string>();
            public int FailuresLeft;

            public Task RecordBanAsync(BanEvent ban)
            {
                if (FailuresLeft-- > 0) throw new InvalidOperationException("database down");
                Calls.Add($"ban {ban.Kind} {ban.ChatId} {ban.UserId} {ban.Reason} {ban.ActorId}");
                return Task.CompletedTask;
            }

            public Task<bool> RecordUnbanAsync(UnbanEvent unban)
            {
                Calls.Add($"unban {unban.End} {unban.ChatId} {unban.UserId} {unban.ActorId}");
                return Task.FromResult(true);
            }

            public Task<IReadOnlyList<BanRecord>> GetHistoryAsync(long chatId, long userId, int limit) =>
                throw new NotSupportedException();
        }

        private IBanHistoryRepository _savedRepository;
        private Func<bool> _savedEnabled;
        private TimeSpan _savedDelay;
        private FakeHistory _fake;

        [TestInitialize]
        public void Setup()
        {
            _savedRepository = Repositories.BanHistory;
            _savedEnabled = BanHistory.Enabled;
            _savedDelay = BanHistory.RetryDelay;
            _fake = new FakeHistory();
            Repositories.BanHistory = _fake;
            BanHistory.Enabled = () => true;
            BanHistory.RetryDelay = TimeSpan.Zero;
        }

        [TestCleanup]
        public void Cleanup()
        {
            Repositories.BanHistory = _savedRepository;
            BanHistory.Enabled = _savedEnabled;
            BanHistory.RetryDelay = _savedDelay;
        }

        [TestMethod]
        public async Task Writes_InTheOrderTheyHappened()
        {
            BanHistory.Banned(-100, 1, BanKind.Ban, "spam", 7);
            BanHistory.Unbanned(-100, 1, BanEnd.Unbanned, 7);
            BanHistory.Banned(-100, 1, BanKind.Tempban, null, null, DateTime.UtcNow.AddHours(1));
            BanHistory.Unbanned(-100, 1, BanEnd.Expired, null);
            await BanHistory.FlushAsync();

            CollectionAssert.AreEqual(new[]
            {
                "ban Ban -100 1 spam 7",
                "unban Unbanned -100 1 7",
                "ban Tempban -100 1  ",
                "unban Expired -100 1 ",
            }, _fake.Calls);
        }

        [TestMethod]
        public async Task Disabled_WritesNothing()
        {
            BanHistory.Enabled = () => false;
            BanHistory.Banned(-100, 1, BanKind.Ban, "spam", 7);
            BanHistory.Enabled = () => true;
            await BanHistory.FlushAsync();
            Assert.AreEqual(0, _fake.Calls.Count);
        }

        [TestMethod]
        public async Task TransientFailure_IsRetried()
        {
            _fake.FailuresLeft = BanHistory.Attempts - 1;
            BanHistory.Banned(-100, 1, BanKind.Ban, "spam", null);
            await BanHistory.FlushAsync();
            Assert.AreEqual(1, _fake.Calls.Count);
        }

        [TestMethod]
        public async Task PersistentFailure_IsDropped_AndLaterWritesContinue()
        {
            _fake.FailuresLeft = BanHistory.Attempts;
            BanHistory.Banned(-100, 1, BanKind.Ban, "lost", null);
            BanHistory.Unbanned(-100, 2, BanEnd.Unbanned, 7);
            await BanHistory.FlushAsync();
            CollectionAssert.AreEqual(new[] { "unban Unbanned -100 2 7" }, _fake.Calls);
        }

        [TestMethod]
        public async Task LongReasons_AreShortened()
        {
            BanHistory.Banned(-100, 1, BanKind.Ban, new string('x', 2000), null);
            await BanHistory.FlushAsync();
            Assert.IsTrue(_fake.Calls.Single().Length < 600);
        }

        [DataTestMethod]
        [DataRow(-1001234567890L, "supergroup")]
        [DataRow(-123456L, "group")]
        [DataRow(42L, "unknown")]
        public void ChatType_FromTheId(long chatId, string expected)
        {
            Assert.AreEqual(expected, PostgresBanHistoryRepository.ChatType(chatId));
        }
    }

    /// <summary>The SQL against a real PostgreSQL; skipped without ENFORCER_TEST_POSTGRES.</summary>
    [TestClass]
    public class PostgresBanHistoryRepositoryTests
    {
        private string _connectionString;
        private PostgresDatabase _database;
        private PostgresBanHistoryRepository _repo;
        private static readonly DateTimeOffset T0 = new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

        [TestInitialize]
        public async Task Setup()
        {
            _connectionString = await TestPostgres.CreateSchemaAsync();
            await SchemaMigrationRunner.ApplyAsync(_connectionString);
            _database = new PostgresDatabase(_connectionString);
            _repo = new PostgresBanHistoryRepository(() => _database);
        }

        [TestCleanup]
        public async Task Cleanup()
        {
            if (_database != null) await _database.DisposeAsync();
            if (_connectionString != null) await TestPostgres.DropSchemaAsync(_connectionString);
        }

        private Task Ban(long chat, long user, DateTimeOffset at, BanKind kind = BanKind.Ban, long? actor = null,
            string reason = "spam", DateTimeOffset? expires = null) =>
            _repo.RecordBanAsync(new BanEvent(chat, user, kind, reason, actor, at, expires, "normal"));

        [TestMethod]
        public async Task Ban_ThenUnban_IsHistory()
        {
            await Ban(-1001, 5, T0, actor: 9);
            Assert.IsTrue(await _repo.RecordUnbanAsync(new UnbanEvent(-1001, 5, BanEnd.Unbanned, 9, T0.AddHours(1))));

            var history = await _repo.GetHistoryAsync(-1001, 5, 10);
            Assert.AreEqual(1, history.Count);
            var ban = history[0];
            Assert.AreEqual("ban", ban.Kind);
            Assert.AreEqual("revoked", ban.Status);
            Assert.AreEqual("spam", ban.Reason);
            Assert.AreEqual(9L, ban.ActorId);
            Assert.AreEqual(T0, ban.ImposedAt);
            Assert.AreEqual(T0.AddHours(1), ban.ResolvedAt);
            Assert.AreEqual("normal", ban.Edition);
        }

        [TestMethod]
        public async Task NewBan_ClosesTheOpenOne_NewestFirst()
        {
            await Ban(-1001, 5, T0, reason: "first");
            await Ban(-1001, 5, T0.AddHours(1), BanKind.Tempban, reason: "second", expires: T0.AddHours(2));

            var history = await _repo.GetHistoryAsync(-1001, 5, 10);
            Assert.AreEqual(2, history.Count);
            Assert.AreEqual("second", history[0].Reason);
            Assert.AreEqual("active", history[0].Status);
            Assert.AreEqual(T0.AddHours(2), history[0].ExpiresAt);
            Assert.AreEqual("first", history[1].Reason);
            Assert.AreEqual("revoked", history[1].Status);
            Assert.IsNull(history[1].ActorId);
        }

        [TestMethod]
        public async Task ExpiredTempban_IsCompleted()
        {
            await Ban(-1001, 5, T0, BanKind.Tempban, expires: T0.AddMinutes(30));
            Assert.IsTrue(await _repo.RecordUnbanAsync(new UnbanEvent(-1001, 5, BanEnd.Expired, null, T0.AddMinutes(30))));
            Assert.AreEqual("completed", (await _repo.GetHistoryAsync(-1001, 5, 10)).Single().Status);
        }

        [TestMethod]
        public async Task Unban_WithoutARecordedBan_WritesNothing()
        {
            Assert.IsFalse(await _repo.RecordUnbanAsync(new UnbanEvent(-1001, 5, BanEnd.Unbanned, 9, T0)));
            await Ban(-1001, 6, T0);
            Assert.IsFalse(await _repo.RecordUnbanAsync(new UnbanEvent(-1001, 5, BanEnd.Unbanned, 9, T0)));
            Assert.AreEqual(0, (await _repo.GetHistoryAsync(-1001, 5, 10)).Count);
        }

        [TestMethod]
        public async Task History_IsPerGroup()
        {
            await Ban(-1001, 5, T0, reason: "here");
            await Ban(-1002, 5, T0, reason: "elsewhere");
            await _repo.RecordUnbanAsync(new UnbanEvent(-1002, 5, BanEnd.Unbanned, null, T0.AddHours(1)));

            var here = await _repo.GetHistoryAsync(-1001, 5, 10);
            Assert.AreEqual("here", here.Single().Reason);
            Assert.AreEqual("active", here.Single().Status);
        }

        [TestMethod]
        public async Task SkewedClock_DoesNotBreakThePeriodCheck()
        {
            // The other edition's clock is a little behind: the unban arrives "before" the ban.
            await Ban(-1001, 5, T0);
            Assert.IsTrue(await _repo.RecordUnbanAsync(new UnbanEvent(-1001, 5, BanEnd.Unbanned, null, T0.AddSeconds(-2))));
            await Ban(-1001, 5, T0.AddSeconds(5));
            await Ban(-1001, 5, T0.AddSeconds(3));
            Assert.AreEqual(3, (await _repo.GetHistoryAsync(-1001, 5, 10)).Count);
        }
    }
}
