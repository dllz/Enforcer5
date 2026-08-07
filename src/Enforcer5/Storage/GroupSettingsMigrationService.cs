using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Helpers;
using Newtonsoft.Json;

namespace Enforcer5.Storage
{
    internal sealed class GroupSettingsMigrationService
    {
        private readonly PostgresDatabase _database;
        private readonly GroupSettingsRepository _repository;

        internal GroupSettingsMigrationService(PostgresDatabase database)
        {
            _database = database;
            _repository = new GroupSettingsRepository(database);
        }

        internal async Task<GroupSettingsMigrationResult> RunAsync(bool reconcile,
            System.Threading.CancellationToken cancellationToken = default)
        {
            var groups = await Redis.db.SetMembersAsync("bot:groupsid");
            var result = new GroupSettingsMigrationResult(groups.Length * GroupSettingGroups.All.Count);

            foreach (var groupValue in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!long.TryParse(groupValue.ToString(), out var chatId))
                {
                    result.InvalidGroups++;
                    continue;
                }

                foreach (var settingGroup in GroupSettingGroups.All)
                {
                    try
                    {
                        var snapshot = await GroupSettingsStore.ObserveAsync(chatId, settingGroup);
                        var comparison = reconcile ? await CompareAsync(snapshot) : null;
                        var different = comparison?.Different == true;
                        if (different) result.Mismatches++;
                        var applied = await _repository.UpsertAsync(snapshot,
                            force: reconcile && different,
                            expectedCurrentRevision: comparison?.SourceRevision);
                        if (!applied && different)
                        {
                            // PostgreSQL changed after comparison. Re-observe Redis and retry through
                            // normal revision ordering rather than overwriting the concurrent write.
                            snapshot = await GroupSettingsStore.ObserveAsync(chatId, settingGroup);
                            applied = await _repository.UpsertAsync(snapshot);
                            if (!applied && (await CompareAsync(snapshot)).Different)
                                throw new InvalidOperationException("Concurrent settings reconciliation did not converge.");
                        }
                        result.Processed++;
                    }
                    catch (Exception e)
                    {
                        result.Failures++;
                        Console.Error.WriteLine($"[ERROR] chat:{chatId}:{settingGroup}: {e.Message}");
                    }
                }
            }

            return result;
        }

        private async Task<SettingsComparison> CompareAsync(GroupSettingsSnapshot snapshot)
        {
            await using var connection = await _database.OpenConnectionAsync();
            var current = await connection.QuerySingleOrDefaultAsync<StoredSettings>(@"
SELECT sync.source_exists AS SourceExists,
       sync.source_checksum AS SourceChecksum,
       sync.source_revision AS SourceRevision,
       (canonical.chat_id IS NOT NULL) AS CanonicalExists,
       canonical.values::text AS CanonicalValues
FROM group_settings_redis_sync sync
LEFT JOIN group_settings canonical
  ON canonical.chat_id = sync.chat_id AND canonical.setting_group = sync.setting_group
WHERE sync.chat_id = @ChatId AND sync.setting_group = @SettingGroup",
                new { snapshot.ChatId, snapshot.SettingGroup });
            if (current == null)
                return new SettingsComparison(true, null);

            var canonicalChecksum = current.CanonicalExists
                ? GroupSettingsSnapshot.CalculateChecksum(
                    JsonConvert.DeserializeObject<Dictionary<string, string>>(current.CanonicalValues)
                    ?? new Dictionary<string, string>())
                : GroupSettingsSnapshot.CalculateChecksum(new Dictionary<string, string>());
            var different = current.SourceExists != snapshot.SourceExists ||
                            current.CanonicalExists != snapshot.SourceExists ||
                            !string.Equals(current.SourceChecksum, snapshot.Checksum, StringComparison.Ordinal) ||
                            (snapshot.SourceExists &&
                             !string.Equals(canonicalChecksum, snapshot.Checksum, StringComparison.Ordinal));
            return new SettingsComparison(different, current.SourceRevision);
        }

        private sealed record SettingsComparison(bool Different, long? SourceRevision);

        private sealed class StoredSettings
        {
            public bool SourceExists { get; set; }
            public string SourceChecksum { get; set; }
            public long SourceRevision { get; set; }
            public bool CanonicalExists { get; set; }
            public string CanonicalValues { get; set; }
        }
    }

    internal sealed class GroupSettingsMigrationResult
    {
        internal GroupSettingsMigrationResult(int expected) => Expected = expected;
        internal int Expected { get; }
        internal int Processed { get; set; }
        internal int Mismatches { get; set; }
        internal int Failures { get; set; }
        internal int InvalidGroups { get; set; }
    }
}
