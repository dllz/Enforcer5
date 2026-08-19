using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Enforcer5.Storage.Loading;
using Enforcer5.Storage.Moderation;
using Enforcer5.Storage.Operations;
using Enforcer5.Storage.PrincipalContent;

namespace Enforcer5.Storage.Cutover
{
    internal sealed record GroupCutoverResult(long TelegramChatId, GroupMigrationState FinalState,
        int DrainedKeys, TimeSpan Elapsed, string Failure)
    {
        internal bool Succeeded => FinalState == GroupMigrationState.Postgres;
    }

    /// <summary>
    /// Moves one group from Redis to PostgreSQL.
    /// <para>
    /// <c>Redis -&gt; Draining -&gt; Verifying -&gt; Postgres</c>, with <c>Failed -&gt; Redis</c>.
    /// Redis stays authoritative until the very last transition, which is what makes a failed
    /// attempt free: the group carries on being served from Redis and the PostgreSQL rows it wrote
    /// are inert garbage that the next attempt overwrites.
    /// </para>
    /// <para>
    /// The bulk of the copy runs <em>outside</em> any lock, so a large group does not freeze. Live
    /// mutations during that window are captured by <see cref="GroupDrainBuffer"/> and replayed
    /// before the flip. Freeze duration is therefore proportional to the drain buffer, never to the
    /// group's size - which is what makes the 528,056-key outlier no worse than the median.
    /// </para>
    /// </summary>
    internal sealed class GroupCutoverService
    {
        /// <summary>
        /// How long a lease is held before another migrator may reclaim it. Long enough that a slow
        /// group is not stolen mid-copy, short enough that a crashed migrator does not strand it.
        /// </summary>
        private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

        /// <summary>
        /// Drain passes before giving up. A group mutating faster than it can be drained will never
        /// converge, and looping forever would hold its lease indefinitely.
        /// </summary>
        private const int MaxDrainPasses = 20;

        private const int DrainBatchSize = 5_000;

        private readonly PostgresDatabase _database;
        private readonly string _owner;

        internal GroupCutoverService(PostgresDatabase database, string owner)
        {
            _database = database;
            _owner = owner;
        }

        internal async Task<GroupCutoverResult> CutOverAsync(long telegramChatId,
            CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            var lease = await GroupMigrationStateStore.TryAcquireLeaseAsync(_database, telegramChatId,
                _owner, LeaseDuration, cancellationToken);
            if (lease == null)
                return new GroupCutoverResult(telegramChatId, GroupMigrationStateStore.Get(telegramChatId),
                    0, stopwatch.Elapsed, "another migrator holds the lease");

            var drained = 0;
            try
            {
                // The freshness rule: a group may only cut over against an inventory that is newer
                // than the moment it started draining. From that instant the buffer captures every
                // new key, so the inventory only has to cover everything before it.
                var generation = await MigrationInventory.LatestAsync(_database, cancellationToken);
                if (generation == null)
                    return await FailAsync(lease, stopwatch, drained, "no completed keyspace inventory");

                if (!await GroupMigrationStateStore.TransitionAsync(_database, lease,
                        GroupMigrationState.Draining, cancellationToken: cancellationToken))
                    return await FailAsync(lease, stopwatch, drained, "could not enter Draining");

                var enteredDrainingAt = DateTimeOffset.UtcNow;

                await CopyAsync(telegramChatId, cancellationToken);
                drained = await DrainAsync(telegramChatId, cancellationToken);
                if (drained < 0)
                    return await FailAsync(lease, stopwatch, 0, "drain did not converge");

                if (generation.Value.CompletedAt < enteredDrainingAt)
                {
                    // The inventory predates the drain, so keys created between the two are in
                    // neither. Re-scanning is the crawler's job; this group waits for the next
                    // generation rather than cutting over against a hole.
                    return await FailAsync(lease, stopwatch, drained,
                        "inventory is older than the drain start");
                }

                if (!await GroupMigrationStateStore.TransitionAsync(_database, lease,
                        GroupMigrationState.Verifying, cancellationToken: cancellationToken))
                    return await FailAsync(lease, stopwatch, drained, "could not enter Verifying");

                var mismatches = await VerifyAsync(telegramChatId, cancellationToken);
                if (mismatches != 0)
                    return await FailAsync(lease, stopwatch, drained,
                        $"reconciliation found {mismatches} mismatch(es)");

                // Anything appended during verification still has to be replayed - Redis was
                // authoritative for the whole of it.
                var late = await DrainAsync(telegramChatId, cancellationToken);
                if (late < 0)
                    return await FailAsync(lease, stopwatch, drained, "late drain did not converge");
                drained += late;

                if (!await GroupMigrationStateStore.TransitionAsync(_database, lease,
                        GroupMigrationState.Postgres, cancellationToken: cancellationToken))
                    return await FailAsync(lease, stopwatch, drained, "could not enter Postgres");

                await GroupDrainBuffer.DeleteAsync(telegramChatId);
                LogHelper.Info($"Group {telegramChatId} cut over in {stopwatch.Elapsed.TotalSeconds:N1}s " +
                               $"({drained} drained key(s)).");
                return new GroupCutoverResult(telegramChatId, GroupMigrationState.Postgres, drained,
                    stopwatch.Elapsed, null);
            }
            catch (OperationCanceledException)
            {
                await SafeRevertAsync(telegramChatId, "cancelled");
                throw;
            }
            catch (Exception e)
            {
                return await FailAsync(lease, stopwatch, drained, e.Message);
            }
            finally
            {
                await GroupMigrationStateStore.ReleaseLeaseAsync(_database, lease, CancellationToken.None);
            }
        }

        /// <summary>
        /// Copies the group through the existing per-domain backfills, which already accept a single
        /// group. Reusing them rather than writing a per-group copier keeps one implementation of
        /// each domain's mapping, so a fix reaches both the bulk path and the cutover.
        /// </summary>
        private async Task CopyAsync(long telegramChatId, CancellationToken cancellationToken)
        {
            var scope = await new LegacyGroupScopeReader(Redis.db).ReadAsync(telegramChatId,
                includeReports: true, cancellationToken);
            var groups = new[] { telegramChatId };

            await new GroupSettingsMigrationService(_database).RunAsync(groups, false, cancellationToken);
            await new PrincipalContentBackfillService(_database).RunRedisPrimaryAsync(
                scope.UserIds, groups, false, cancellationToken);
            await new ModerationBackfillService(_database).RunRedisPrimaryAsync(groups, false,
                includeGlobalRoles: false, cancellationToken);
            await new OperationsBackfillService(_database).RunRedisPrimaryAsync(groups, scope.UserIds,
                new Dictionary<long, IReadOnlyCollection<long>> { [telegramChatId] = scope.ReportMessageIds },
                false, cancellationToken, includeGlobalData: false);
        }

        /// <summary>
        /// Replays buffered mutations until the buffer stays empty, and reports how many keys were
        /// replayed. Returns -1 when the group mutates faster than it can be drained.
        /// </summary>
        private async Task<int> DrainAsync(long telegramChatId, CancellationToken cancellationToken)
        {
            var total = 0;
            for (var pass = 0; pass < MaxDrainPasses; pass++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = await GroupDrainBuffer.PeekAsync(telegramChatId, DrainBatchSize);
                if (batch.Count == 0) return total;

                // Re-copy rather than replay: the buffer holds coordinates, so re-reading converges
                // on current truth however many times a key changed.
                await CopyAsync(telegramChatId, cancellationToken);
                await GroupDrainBuffer.TrimAsync(telegramChatId, batch.Count);
                total += batch.Count;
            }
            return await GroupDrainBuffer.LengthAsync(telegramChatId) == 0 ? total : -1;
        }

        /// <summary>
        /// Reconciles the group in both directions. This is the gate the flip depends on, which is
        /// why it had to stop being able to report a false clean.
        /// </summary>
        private async Task<int> VerifyAsync(long telegramChatId, CancellationToken cancellationToken)
        {
            var groups = new[] { telegramChatId };
            var settings = await new GroupSettingsMigrationService(_database)
                .RunAsync(groups, true, cancellationToken);
            if (settings.Mismatches != 0 || settings.InvalidGroups != 0)
                return settings.Mismatches + settings.InvalidGroups;

            var moderation = new ModerationBackfillService(_database);
            await moderation.RunRedisPrimaryAsync(groups, true, includeGlobalRoles: false, cancellationToken);
            if (moderation.ReconciliationMismatches != 0) return moderation.ReconciliationMismatches;

            var scope = await new LegacyGroupScopeReader(Redis.db).ReadAsync(telegramChatId,
                includeReports: true, cancellationToken);
            var principal = new PrincipalContentBackfillService(_database);
            await principal.RunRedisPrimaryAsync(scope.UserIds, groups, true, cancellationToken);
            if (principal.ReconciliationMismatches != 0) return principal.ReconciliationMismatches;

            var operations = new OperationsBackfillService(_database);
            await operations.RunRedisPrimaryAsync(groups, scope.UserIds,
                new Dictionary<long, IReadOnlyCollection<long>> { [telegramChatId] = scope.ReportMessageIds },
                true, cancellationToken, includeGlobalData: false);
            return operations.ReconciliationMismatches;
        }

        /// <summary>
        /// Puts the group back on Redis. The PostgreSQL rows are deliberately left in place: they
        /// are unreachable while the group reads from Redis, and inspecting them is more useful
        /// than deleting them.
        /// </summary>
        private async Task<GroupCutoverResult> FailAsync(GroupLease lease, Stopwatch stopwatch,
            int drained, string failure)
        {
            LogHelper.Error($"Group {lease.TelegramChatId} cutover failed: {failure}.");
            await SafeRevertAsync(lease.TelegramChatId, failure);
            return new GroupCutoverResult(lease.TelegramChatId, GroupMigrationState.Redis, drained,
                stopwatch.Elapsed, failure);
        }

        private async Task SafeRevertAsync(long telegramChatId, string reason)
        {
            try
            {
                await GroupMigrationStateStore.AbandonAsync(_database, telegramChatId, reason);
                await GroupDrainBuffer.DeleteAsync(telegramChatId);
            }
            catch (Exception e)
            {
                // The in-memory state is already Redis, so the group is being served correctly even
                // if the row and the buffer are stale.
                GroupMigrationStateStore.ApplyLocal(telegramChatId, GroupMigrationState.Redis);
                LogHelper.Error($"Reverting group {telegramChatId} failed: {e.Message}");
            }
        }
    }
}
