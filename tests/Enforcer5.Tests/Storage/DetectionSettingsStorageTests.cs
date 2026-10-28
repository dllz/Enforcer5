using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Data;
using Enforcer5.Detection;
using Enforcer5.Models;
using Enforcer5.Storage;
using Enforcer5.Tests.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Storage
{
    /// <summary>
    /// Detection settings in PostgreSQL (Detection step 2); skipped without ENFORCER_TEST_POSTGRES.
    /// </summary>
    [TestClass]
    [TestCategory("Detection")]
    public class PostgresDetectionSettingsRepositoryTests
    {
        private const long ChatId = -1001500;
        private string _connectionString;
        private PostgresDatabase _database;
        private PostgresDetectionSettingsRepository _repo;

        [TestInitialize]
        public async Task Setup()
        {
            _connectionString = await TestPostgres.MigratedSchemaAsync();
            _database = new PostgresDatabase(_connectionString);
            _repo = new PostgresDetectionSettingsRepository(() => _database);
        }

        [TestCleanup]
        public async Task Cleanup()
        {
            if (_database != null) await _database.DisposeAsync();
            if (_connectionString != null) await TestPostgres.ReleaseMigratedAsync(_connectionString);
        }

        private async Task<long[]> FilterIds(string target)
        {
            await using var connection = await _database.OpenConnectionAsync();
            return (await connection.QueryAsync<long>(
                "SELECT filter_id FROM group_filters WHERE target = @Target ORDER BY filter_id", new { Target = target })).ToArray();
        }

        [TestMethod]
        public async Task AGroupNeverImported_HasNoSettings()
        {
            Assert.IsNull(await _repo.LoadAsync(ChatId));
            Assert.IsNull(await _repo.SaveAsync(ChatId, DetectionPolicy.Empty, 1), "nothing to save over");
        }

        [TestMethod]
        public async Task Import_ThenLoad_GivesBackEverySetting()
        {
            var policy = DetectionPolicyJsonTests.Everything();
            var imported = await _repo.ImportAsync(ChatId, policy);
            Assert.AreEqual(1, imported.Version);

            var loaded = await _repo.LoadAsync(ChatId);
            Assert.AreEqual(1, loaded.Version);
            Assert.IsTrue(loaded.SameSettingsAs(policy),
                $"stored:\n{DetectionPolicyJson.Serialize(policy with { Version = 0 })}\nloaded:\n{DetectionPolicyJson.Serialize(loaded with { Version = 0 })}");
        }

        [TestMethod]
        public async Task Import_OfNothingStored_RoundTripsTheNulls()
        {
            await _repo.ImportAsync(ChatId, DetectionPolicy.Empty);
            var loaded = await _repo.LoadAsync(ChatId);
            Assert.IsTrue(loaded.SameSettingsAs(DetectionPolicy.Empty));
            Assert.IsNull(loaded.Flood.MaxMessages);
            Assert.IsNull(loaded.TextLength.Action);
        }

        [TestMethod]
        public async Task ASecondImport_KeepsTheFirst()
        {
            await _repo.ImportAsync(ChatId, DetectionPolicy.Empty with { MuteOnJoin = true });
            var second = await _repo.ImportAsync(ChatId, DetectionPolicy.Empty);
            Assert.IsTrue(second.MuteOnJoin);
            Assert.AreEqual(1, second.Version);
        }

        [TestMethod]
        public async Task Save_BumpsTheVersion_AndRefusesAStaleOne()
        {
            var first = await _repo.ImportAsync(ChatId, DetectionPolicy.Empty);
            var saved = await _repo.SaveAsync(ChatId, first with { MuteOnJoin = true }, first.Version);
            Assert.AreEqual(2, saved.Version);
            Assert.IsNull(await _repo.SaveAsync(ChatId, first with { TempbanMinutes = 5 }, first.Version), "someone saved in between");

            var loaded = await _repo.LoadAsync(ChatId);
            Assert.IsTrue(loaded.MuteOnJoin);
            Assert.AreEqual(first.TempbanMinutes, loaded.TempbanMinutes);
        }

        [TestMethod]
        public async Task Save_KeepsTheRowsOfEntriesThatStay_AndDropsOnlyTheRemovedOnes()
        {
            var gif = new InlineBotBlock(InlineBotBlockKind.Username, "gif");
            var pattern = new InlineBotBlock(InlineBotBlockKind.Pattern, "^spam");
            var first = await _repo.ImportAsync(ChatId, DetectionPolicy.Empty with { InlineBots = new[] { gif, pattern } });
            var before = await FilterIds("inline_bot");

            var vid = new InlineBotBlock(InlineBotBlockKind.Username, "vid");
            await _repo.SaveAsync(ChatId, first with { InlineBots = new[] { pattern, vid } }, first.Version);

            var after = await FilterIds("inline_bot");
            Assert.AreEqual(2, after.Length);
            Assert.AreEqual(before[1], after[0], "the pattern kept its row");
            var loaded = await _repo.LoadAsync(ChatId);
            CollectionAssert.AreEquivalent(new[] { pattern, vid }, loaded.InlineBots.ToArray());
        }

        [TestMethod]
        public async Task TheScriptsAndLengths_ArePresetsAndLengthRulesInGroupFilters()
        {
            await _repo.ImportAsync(ChatId, DetectionPolicy.Empty with
            {
                Filters = FilterSettings.Empty.WithPreset(FilterPresets.Arabic, "ban").WithPreset(FilterPresets.Rtl, null),
                TextLength = new LengthSettings(true, 100, 20, "warn")
            });
            await using var connection = await _database.OpenConnectionAsync();
            var rows = (await connection.QueryAsync<(string Kind, string Preset, string Target, bool Enabled, string Action)>(
                "SELECT kind, preset, target, enabled, action FROM group_filters WHERE kind IN ('preset', 'length') ORDER BY kind, preset, target")).ToArray();
            CollectionAssert.AreEqual(new[]
            {
                ("length", (string)null, "name", false, (string)null),
                ("length", null, "text", true, "warn"),
                ("preset", "arabic", null, true, "ban"),
                ("preset", "rtl", null, false, null)
            }, rows);
        }
    }
}
