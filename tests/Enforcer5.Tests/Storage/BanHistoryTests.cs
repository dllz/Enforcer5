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
            public Task<IReadOnlyList<BanRecord>> GetSanctionsAsync(long chatId, long userId, int limit) => throw new NotSupportedException();
            public Task<bool> RecordUnmuteAsync(UnmuteEvent unmute) => Task.FromResult(true);
        }

        private IBanHistoryRepository _savedRepository;
        private Func<bool> _savedEnabled;
        private TimeSpan _savedDelay;
        private FakeHistory _fake;

        [TestInitialize]
        public void Setup()
        {
            _savedRepository = Repositories.BanHistory;
            _savedEnabled = HistoryWriter.Enabled;
            _savedDelay = HistoryWriter.RetryDelay;
            _fake = new FakeHistory();
            Repositories.BanHistory = _fake;
            HistoryWriter.Enabled = () => true;
            HistoryWriter.RetryDelay = TimeSpan.Zero;
        }

        [TestCleanup]
        public void Cleanup()
        {
            Repositories.BanHistory = _savedRepository;
            HistoryWriter.Enabled = _savedEnabled;
            HistoryWriter.RetryDelay = _savedDelay;
        }

        [TestMethod]
        public async Task Writes_InTheOrderTheyHappened()
        {
            BanHistory.Banned(-100, 1, SanctionKind.Ban, "spam", 7);
            BanHistory.Unbanned(-100, 1, BanEnd.Unbanned, 7);
            BanHistory.Banned(-100, 1, SanctionKind.Tempban, null, null, DateTime.UtcNow.AddHours(1));
            BanHistory.Unbanned(-100, 1, BanEnd.Expired, null);
            await HistoryWriter.FlushAsync();

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
            HistoryWriter.Enabled = () => false;
            BanHistory.Banned(-100, 1, SanctionKind.Ban, "spam", 7);
            HistoryWriter.Enabled = () => true;
            await HistoryWriter.FlushAsync();
            Assert.AreEqual(0, _fake.Calls.Count);
        }

        [TestMethod]
        public async Task TransientFailure_IsRetried()
        {
            _fake.FailuresLeft = HistoryWriter.Attempts - 1;
            BanHistory.Banned(-100, 1, SanctionKind.Ban, "spam", null);
            await HistoryWriter.FlushAsync();
            Assert.AreEqual(1, _fake.Calls.Count);
        }

        [TestMethod]
        public async Task PersistentFailure_IsDropped_AndLaterWritesContinue()
        {
            _fake.FailuresLeft = HistoryWriter.Attempts;
            BanHistory.Banned(-100, 1, SanctionKind.Ban, "lost", null);
            BanHistory.Unbanned(-100, 2, BanEnd.Unbanned, 7);
            await HistoryWriter.FlushAsync();
            CollectionAssert.AreEqual(new[] { "unban Unbanned -100 2 7" }, _fake.Calls);
        }

        [TestMethod]
        public async Task LongReasons_AreShortened()
        {
            BanHistory.Banned(-100, 1, SanctionKind.Ban, new string('x', 2000), null);
            await HistoryWriter.FlushAsync();
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
            _connectionString = await TestPostgres.MigratedSchemaAsync();
            _database = new PostgresDatabase(_connectionString);
            _repo = new PostgresBanHistoryRepository(() => _database);
        }

        [TestCleanup]
        public async Task Cleanup()
        {
            if (_database != null) await _database.DisposeAsync();
            if (_connectionString != null) await TestPostgres.ReleaseMigratedAsync(_connectionString);
        }

        private Task Ban(long chat, long user, DateTimeOffset at, SanctionKind kind = SanctionKind.Ban, long? actor = null,
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
            await Ban(-1001, 5, T0.AddHours(1), SanctionKind.Tempban, reason: "second", expires: T0.AddHours(2));

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
            await Ban(-1001, 5, T0, SanctionKind.Tempban, expires: T0.AddMinutes(30));
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

        [TestMethod]
        public async Task Kick_IsClosedAtOnce_AndLiftsAnOpenBan()
        {
            await Ban(-1001, 5, T0, reason: "old");
            await Ban(-1001, 5, T0.AddHours(1), SanctionKind.Kick, actor: 9, reason: "rude");

            var all = await _repo.GetSanctionsAsync(-1001, 5, 10);
            Assert.AreEqual(("kick", "completed", "rude", (long?)9), (all[0].Kind, all[0].Status, all[0].Reason, all[0].ActorId));
            Assert.AreEqual(T0.AddHours(1), all[0].ResolvedAt);
            Assert.IsNull(all[0].ExpiresAt);
            Assert.AreEqual(("ban", "revoked"), (all[1].Kind, all[1].Status));
            // The ban history only lists bans.
            Assert.AreEqual(1, (await _repo.GetHistoryAsync(-1001, 5, 10)).Count);
        }

        [TestMethod]
        public async Task Mute_ThenUnmute_IsRevoked_AndLeavesBansAlone()
        {
            await Ban(-1001, 5, T0, reason: "banned");
            await Ban(-1001, 5, T0.AddMinutes(1), SanctionKind.Mute, actor: 9, reason: "shouting");
            Assert.IsTrue(await _repo.RecordUnmuteAsync(new UnmuteEvent(-1001, 5, 9, T0.AddMinutes(5))));

            var all = await _repo.GetSanctionsAsync(-1001, 5, 10);
            Assert.AreEqual(("mute", "revoked"), (all[0].Kind, all[0].Status));
            Assert.AreEqual(T0.AddMinutes(5), all[0].ResolvedAt);
            Assert.AreEqual(("ban", "active"), (all[1].Kind, all[1].Status));
        }

        [TestMethod]
        public async Task NewMute_ClosesARunOutTempmuteAsCompleted_AndALiveOneAsRevoked()
        {
            await Ban(-1001, 5, T0, SanctionKind.Tempmute, expires: T0.AddMinutes(10));
            await Ban(-1001, 5, T0.AddHours(1), SanctionKind.Tempmute, expires: T0.AddHours(3));
            await Ban(-1001, 5, T0.AddHours(2), SanctionKind.Mute);

            var all = await _repo.GetSanctionsAsync(-1001, 5, 10);
            Assert.AreEqual(("mute", "active"), (all[0].Kind, all[0].Status));
            Assert.AreEqual(("tempmute", "revoked", (DateTimeOffset?)T0.AddHours(2)), (all[1].Kind, all[1].Status, all[1].ResolvedAt));
            Assert.AreEqual(("tempmute", "completed", (DateTimeOffset?)T0.AddMinutes(10)), (all[2].Kind, all[2].Status, all[2].ResolvedAt));
        }

        [TestMethod]
        public async Task Unmute_WithoutARecordedMute_WritesNothing()
        {
            Assert.IsFalse(await _repo.RecordUnmuteAsync(new UnmuteEvent(-1001, 5, 9, T0)));
            await Ban(-1001, 5, T0);
            Assert.IsFalse(await _repo.RecordUnmuteAsync(new UnmuteEvent(-1001, 5, 9, T0.AddMinutes(1))));
            Assert.AreEqual("active", (await _repo.GetSanctionsAsync(-1001, 5, 10)).Single().Status);
        }
    }
}
