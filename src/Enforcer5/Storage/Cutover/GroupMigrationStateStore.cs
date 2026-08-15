using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Helpers;

namespace Enforcer5.Storage.Cutover
{
    internal sealed record GroupLease(long TelegramChatId, string Owner, DateTimeOffset ExpiresAt);

    /// <summary>
    /// Per-group cutover state, and the lease that serialises cutover attempts for one group.
    /// <para>
    /// Two things this must get right. It is read on the message hot path, so the common case is an
    /// in-memory lookup rather than a query. And a group's state is only ever advanced by the holder
    /// of its lease, so two migrators - or the two bot editions - cannot cut the same group over
    /// twice.
    /// </para>
    /// </summary>
    internal static class GroupMigrationStateStore
    {
        /// <summary>
        /// Hot-path cache. Absent means <see cref="GroupMigrationState.Redis"/>, which is the
        /// default for the overwhelming majority of groups for most of the migration, so a miss is
        /// the cheap answer rather than the expensive one.
        /// </summary>
        private static readonly ConcurrentDictionary<long, GroupMigrationState> Cache = new();

        private static volatile bool _loaded;
        private static readonly SemaphoreSlim LoadGate = new(1, 1);

        /// <summary>
        /// State for a group, served from cache. Falls back to <see cref="GroupMigrationState.Redis"/>
        /// when the cache has not been primed, so a cold start behaves as if nothing has migrated -
        /// the safe direction, since Redis remains authoritative until a group is explicitly flipped.
        /// </summary>
        internal static GroupMigrationState Get(long telegramChatId) =>
            Cache.TryGetValue(telegramChatId, out var state) ? state : GroupMigrationState.Redis;

        internal static bool PostgresIsAuthoritative(long telegramChatId) =>
            Get(telegramChatId).PostgresIsAuthoritative();

        internal static bool IsPrimed => _loaded;

        /// <summary>
        /// Loads every non-default group state into the cache. Only rows that are not
        /// <see cref="GroupMigrationState.Redis"/> are worth holding, so this stays small until the
        /// migration is well advanced.
        /// </summary>
        internal static async Task PrimeAsync(PostgresDatabase database,
            CancellationToken cancellationToken = default)
        {
            await LoadGate.WaitAsync(cancellationToken);
            try
            {
                await using var connection = await database.OpenConnectionAsync(cancellationToken);
                var rows = await connection.QueryAsync<(long TelegramChatId, string State)>(
                    new CommandDefinition(
                        "SELECT telegram_chat_id, state FROM migration_group_state WHERE state <> 'Redis'",
                        cancellationToken: cancellationToken));

                Cache.Clear();
                foreach (var row in rows)
                    Cache[row.TelegramChatId] = GroupMigrationStateExtensions.Parse(row.State);

                _loaded = true;
                LogHelper.Info($"Group migration state primed: {Cache.Count} group(s) past Redis.");
            }
            finally
            {
                LoadGate.Release();
            }
        }

        /// <summary>Applies a state change observed elsewhere, without a round trip.</summary>
        internal static void ApplyLocal(long telegramChatId, GroupMigrationState state)
        {
            if (state == GroupMigrationState.Redis) Cache.TryRemove(telegramChatId, out _);
            else Cache[telegramChatId] = state;
        }

        internal static void ResetForTests()
        {
            Cache.Clear();
            _loaded = false;
        }

        /// <summary>
        /// Takes the lease for a group, or returns null if another owner holds a live one.
        /// <para>
        /// A lease that has expired is reclaimable, so a migrator that crashed mid-cutover does not
        /// strand the group forever. The whole claim is one statement so two callers cannot both win.
        /// </para>
        /// </summary>
        internal static async Task<GroupLease> TryAcquireLeaseAsync(PostgresDatabase database,
            long telegramChatId, string owner, TimeSpan duration,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(owner))
                throw new ArgumentException("Lease owner is required.", nameof(owner));

            await using var connection = await database.OpenConnectionAsync(cancellationToken);

            // The expiry is computed by PostgreSQL rather than the caller, so the value written and
            // the now() it is later compared against come from one clock. Mixing the application
            // clock with the database clock makes reclaim depend on skew between two hosts.
            var acquired = await connection.QuerySingleOrDefaultAsync<DateTimeOffset?>(
                new CommandDefinition(@"
INSERT INTO migration_group_state (telegram_chat_id, state, lease_owner, lease_expires_at, updated_at)
VALUES (@ChatId, 'Redis', @Owner, now() + @Duration, now())
ON CONFLICT (telegram_chat_id) DO UPDATE
   SET lease_owner = @Owner, lease_expires_at = now() + @Duration, updated_at = now()
 WHERE migration_group_state.lease_owner IS NULL
    OR migration_group_state.lease_owner = @Owner
    OR migration_group_state.lease_expires_at < now()
RETURNING lease_expires_at",
                    new { ChatId = telegramChatId, Owner = owner, Duration = duration },
                    cancellationToken: cancellationToken));

            return acquired.HasValue ? new GroupLease(telegramChatId, owner, acquired.Value) : null;
        }

        internal static async Task ReleaseLeaseAsync(PostgresDatabase database, GroupLease lease,
            CancellationToken cancellationToken = default)
        {
            if (lease == null) return;
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE migration_group_state
   SET lease_owner = NULL, lease_expires_at = NULL, updated_at = now()
 WHERE telegram_chat_id = @ChatId AND lease_owner = @Owner",
                new { ChatId = lease.TelegramChatId, Owner = lease.Owner },
                cancellationToken: cancellationToken));
        }

        /// <summary>
        /// Advances a group's state. Only the lease holder may do so, so a stale migrator that lost
        /// its lease cannot flip a group another one is working on.
        /// </summary>
        internal static async Task<bool> TransitionAsync(PostgresDatabase database, GroupLease lease,
            GroupMigrationState state, string lastError = null,
            CancellationToken cancellationToken = default)
        {
            if (lease == null) throw new ArgumentNullException(nameof(lease));

            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            var updated = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE migration_group_state
   SET state = @State,
       last_error = @LastError,
       migrated_at = CASE WHEN @State = 'Postgres' THEN now() ELSE migrated_at END,
       updated_at = now()
 WHERE telegram_chat_id = @ChatId AND lease_owner = @Owner AND lease_expires_at >= now()",
                new
                {
                    ChatId = lease.TelegramChatId,
                    Owner = lease.Owner,
                    State = state.ToString(),
                    LastError = lastError
                }, cancellationToken: cancellationToken)) == 1;

            if (updated) ApplyLocal(lease.TelegramChatId, state);
            return updated;
        }

        /// <summary>Burn-down: how many groups sit in each state.</summary>
        internal static async Task<IReadOnlyDictionary<GroupMigrationState, int>> CountByStateAsync(
            PostgresDatabase database, CancellationToken cancellationToken = default)
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            var rows = await connection.QueryAsync<(string State, int Count)>(new CommandDefinition(
                "SELECT state, count(*)::int FROM migration_group_state GROUP BY state",
                cancellationToken: cancellationToken));
            return rows.ToDictionary(r => GroupMigrationStateExtensions.Parse(r.State), r => r.Count);
        }
    }
}
