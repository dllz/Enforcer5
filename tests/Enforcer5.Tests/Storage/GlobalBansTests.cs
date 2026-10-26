using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Storage
{
    /// <summary>The in-memory global ban set and the replies built from it; no database.</summary>
    [TestClass]
    [DoNotParallelize] // GlobalBans and Repositories are static
    public class GlobalBansTests
    {
        private sealed class FakeRepository : IGlobalBanRepository
        {
            public readonly List<GlobalBanRecord> Active = new List<GlobalBanRecord>();
            public Func<Task> DuringRead = () => Task.CompletedTask;

            public Task<GlobalBanRecord> BanAsync(long userId, string reason, long? actorId, DateTimeOffset at)
            {
                Active.RemoveAll(b => b.UserId == userId);
                var ban = Ban(userId, reason);
                Active.Add(ban);
                return Task.FromResult(ban);
            }

            public Task<bool> UnbanAsync(long userId, string reason, DateTimeOffset at) =>
                Task.FromResult(Active.RemoveAll(b => b.UserId == userId) > 0);

            public async Task<IReadOnlyList<GlobalBanRecord>> GetActiveAsync()
            {
                var snapshot = Active.ToList();
                await DuringRead();
                return snapshot;
            }

            public Task<GlobalBanRecord> GetLatestAsync(long userId) => throw new NotSupportedException();
            public Task MarkNotifiedAsync(long userId, DateTimeOffset at) => Task.CompletedTask;
        }

        private static GlobalBanRecord Ban(long userId, string reason) =>
            new GlobalBanRecord(userId, "active", reason, null, new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero),
                null, null, null);

        private IGlobalBanRepository _saved;
        private FakeRepository _fake;

        [TestInitialize]
        public void Setup()
        {
            _saved = Repositories.GlobalBans;
            _fake = new FakeRepository();
            Repositories.GlobalBans = _fake;
            GlobalBans.ResetForTests();
        }

        [TestCleanup]
        public void Cleanup()
        {
            Repositories.GlobalBans = _saved;
            GlobalBans.ResetForTests();
        }

        [TestMethod]
        public async Task NothingIsEnforcedUntilLoaded()
        {
            _fake.Active.Add(Ban(1, "spam"));
            Assert.IsFalse(GlobalBans.Loaded);
            Assert.IsNull(GlobalBans.Get(1));

            await GlobalBans.ReloadAsync();

            Assert.IsTrue(GlobalBans.Loaded);
            Assert.AreEqual("spam", GlobalBans.Get(1)?.Reason);
        }

        [TestMethod]
        public async Task BanAndUnban_ApplyAtOnce()
        {
            await GlobalBans.ReloadAsync();

            await GlobalBans.BanAsync(2, "raid", 99);
            Assert.AreEqual("raid", GlobalBans.Get(2)?.Reason);

            Assert.IsTrue(await GlobalBans.UnbanAsync(2, "appeal"));
            Assert.IsNull(GlobalBans.Get(2));
        }

        [TestMethod]
        public async Task AReloadThatReadBeforeABan_DoesNotUndoIt()
        {
            await GlobalBans.ReloadAsync();
            // The reload reads the table, then this process bans someone before it finishes.
            _fake.DuringRead = () => GlobalBans.BanAsync(3, "spam", 99);

            await GlobalBans.ReloadAsync();

            Assert.AreEqual("spam", GlobalBans.Get(3)?.Reason);
            _fake.DuringRead = () => Task.CompletedTask;
            await GlobalBans.ReloadAsync();
            Assert.AreEqual("spam", GlobalBans.Get(3)?.Reason);
        }

        [TestMethod]
        public async Task AFailedReload_KeepsTheLastSet()
        {
            _fake.Active.Add(Ban(4, "spam"));
            await GlobalBans.ReloadAsync();
            _fake.DuringRead = () => throw new InvalidOperationException("database down");

            await Assert.ThrowsExceptionAsync<InvalidOperationException>(GlobalBans.ReloadAsync);

            Assert.AreEqual("spam", GlobalBans.Get(4)?.Reason);
        }

        [TestMethod]
        public void RektList_CountsAndCapsAndEscapes()
        {
            var bans = Enumerable.Range(1, Commands.RektListShown + 5)
                .Select(i => Ban(i, i == 1 ? "<b>spam</b>" : "spam")).ToList();

            var text = Commands.FormatRektList(bans);

            var lines = text.Split('\n');
            Assert.AreEqual($"{bans.Count} active global bans, newest {Commands.RektListShown}:", lines[0]);
            Assert.AreEqual(Commands.RektListShown + 1, lines.Length);
            Assert.AreEqual("1: &lt;b&gt;spam&lt;/b&gt; (2026-10-26)", lines[1]);
            Assert.AreEqual("No active global bans.", Commands.FormatRektList(new List<GlobalBanRecord>()));
        }

        [TestMethod]
        public void GetUser_ShowsTheLatestBan()
        {
            Assert.AreEqual("", Commands.FormatGlobalBan((GlobalBanRecord)null));
            Assert.AreEqual("\nGlobal ban: active since 2026-10-26 12:00 UTC\nGlobal ban reason: spam",
                Commands.FormatGlobalBan(Ban(5, "spam")));
            var lifted = Ban(5, "spam") with
            {
                Status = "revoked", ResolvedAt = new DateTimeOffset(2026, 10, 27, 8, 30, 0, TimeSpan.Zero),
                ResolutionReason = "appeal"
            };
            Assert.AreEqual("\nGlobal ban: lifted 2026-10-27 08:30 UTC\nGlobal ban reason: spam\nLifted because: appeal",
                Commands.FormatGlobalBan(lifted));
        }
    }
}
