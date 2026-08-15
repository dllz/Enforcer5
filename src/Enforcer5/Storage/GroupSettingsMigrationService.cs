using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Helpers;

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
            return await RunAsync(groups.Select(value =>
                long.TryParse(value.ToString(), out var id) ? (long?)id : null)
                .Where(value => value.HasValue).Select(value => value.Value), reconcile, cancellationToken);
        }

        internal async Task<GroupSettingsMigrationResult> RunAsync(IEnumerable<long> groupIds,
            bool reconcile, System.Threading.CancellationToken cancellationToken = default)
        {
            var groups = groupIds.Distinct().ToArray();
            var result = new GroupSettingsMigrationResult(groups.Length * GroupSettingGroups.All.Count);
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            System.Data.Common.DbTransaction batch = null;
            var batchCount = 0;

            try
            {
                batch = await connection.BeginTransactionAsync(cancellationToken);
                foreach (var chatId in groups)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var settingGroup in GroupSettingGroups.All)
                    {
                        var snapshot = await GroupSettingsStore.ObserveAsync(chatId, settingGroup);
                        var different = reconcile && !await _repository.MatchesAsync(snapshot, cancellationToken);
                        if (different) result.Mismatches++;
                        var applied = await GroupSettingsRepository.UpsertAsync(connection, batch, snapshot,
                            cancellationToken,
                            force: reconcile && different,
                            expectedCurrentRevision: null);
                        if (!applied && different)
                        {
                            // PostgreSQL changed after comparison. Re-observe Redis and retry through
                            // normal revision ordering rather than overwriting the concurrent write.
                            snapshot = await GroupSettingsStore.ObserveAsync(chatId, settingGroup);
                            applied = await GroupSettingsRepository.UpsertAsync(connection, batch, snapshot,
                                cancellationToken);
                            if (!applied && !await _repository.MatchesAsync(snapshot, cancellationToken))
                                throw new InvalidOperationException("Concurrent settings reconciliation did not converge.");
                        }
                        result.Processed++;
                        batchCount++;
                        if (batchCount < 500) continue;
                        await batch.CommitAsync(cancellationToken);
                        await batch.DisposeAsync();
                        batch = await connection.BeginTransactionAsync(cancellationToken);
                        batchCount = 0;
                    }
                }
                await batch.CommitAsync(cancellationToken);
            }
            catch (Exception e)
            {
                result.Failures++;
                if (batch != null) await batch.RollbackAsync(System.Threading.CancellationToken.None);
                Console.Error.WriteLine($"[ERROR] Group settings batch failed: {e.Message}");
            }
            finally
            {
                if (batch != null) await batch.DisposeAsync();
            }

            return result;
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
