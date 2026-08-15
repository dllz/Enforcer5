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

        /// <summary>
        /// Migrates every group's settings.
        /// <para>
        /// The population comes from discovery, not from <c>bot:groupsid</c>. That set holds 36,441
        /// members while 38,152 groups have data, because it was only written from one of the three
        /// code paths that can create a settings hash - so seeding from it alone silently skipped
        /// 7,951 groups, all of which have settings to migrate.
        /// </para>
        /// </summary>
        internal async Task<GroupSettingsMigrationResult> RunAsync(bool reconcile,
            System.Threading.CancellationToken cancellationToken = default) =>
            await RunAsync(
                await PrincipalContent.LegacyPrincipalContentReader.DiscoverGroupIdsAsync(cancellationToken),
                reconcile, cancellationToken);

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
                    // Per group, so one unreadable group does not abandon every group after it. The
                    // catch used to sit outside this loop, which meant a single failure aborted the
                    // remainder of the run while reporting Failures = 1.
                    try
                    {
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
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        result.InvalidGroups++;
                        Console.Error.WriteLine($"[ERROR] Group settings failed for {chatId}: {e.Message}");
                        // The batch is poisoned, so start a clean one and carry on with the next group.
                        if (batch != null)
                        {
                            await batch.RollbackAsync(System.Threading.CancellationToken.None);
                            await batch.DisposeAsync();
                        }
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
                Console.Error.WriteLine($"[ERROR] Group settings run failed: {e.Message}");
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
