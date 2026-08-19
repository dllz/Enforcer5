using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Helpers;
using StackExchange.Redis;

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

        /// <summary>
        /// Channel on which a state change is announced to every other process.
        /// <para>
        /// The cache is per process, and there is more than one: the two bot editions, plus any
        /// migration command run alongside them. Without this, a group flipped by one process stays
        /// <see cref="GroupMigrationState.Redis"/> in the others, which is the dangerous direction -
        /// they would keep writing that group to Redis while PostgreSQL is authoritative, and those
        /// writes are lost. The drain buffer does not cover it either, since the buffer is deleted
        /// at the flip.
        /// </para>
        /// </summary>
        private const string StateChannel = "enforcer:migration:group-state";

        /// <summary>
        /// Announces a state change. Best effort: a lost message is corrected by the periodic
        /// re-prime, which is why <see cref="RefreshAsync"/> exists as well.
        /// </summary>
        private static async Task PublishAsync(long telegramChatId, GroupMigrationState state)
        {
            try
            {
                var connection = Helpers.Redis.Connection;
                if (connection == null) return;
                await connection.GetSubscriber().PublishAsync(
                    RedisChannel.Literal(StateChannel), $"{telegramChatId}:{state}");
            }
            catch (Exception e)
            {
                LogHelper.Error($"Announcing group {telegramChatId} state failed: {e.Message}");
            }
        }

        /// <summary>
        /// Subscribes to state changes from other processes. Idempotent.
        /// </summary>
        internal static async Task SubscribeAsync()
        {
            var connection = Helpers.Redis.Connection;
            if (connection == null) return;
            await connection.GetSubscriber().SubscribeAsync(RedisChannel.Literal(StateChannel),
                (_, value) =>
                {
                    var text = value.ToString();
                    var separator = text.LastIndexOf(':');
                    if (separator <= 0) return;
                    if (!long.TryParse(text.AsSpan(0, separator), out var chatId)) return;
                    if (!Enum.TryParse<GroupMigrationState>(text[(separator + 1)..], true, out var state))
                        return;
                    ApplyLocal(chatId, state);
                });
        }

        /// <summary>
        /// Re-reads every non-default state. The safety net under <see cref="PublishAsync"/>: pub/sub
        /// delivery is at-most-once, so a process that was disconnected when a group flipped would
        /// otherwise stay wrong until restart.
        /// </summary>
        internal static Task RefreshAsync(PostgresDatabase database,
            CancellationToken cancellationToken = default) => PrimeAsync(database, cancellationToken);

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

            if (updated)
            {
                ApplyLocal(lease.TelegramChatId, state);
                await PublishAsync(lease.TelegramChatId, state);
            }
            return updated;
        }

        /// <summary>
        /// Takes a group out of the migration without holding its lease.
        /// <para>
        /// Every other transition requires the lease, so that a stale migrator cannot disturb a
        /// group another one is working on. Abandonment is the deliberate exception: it is raised
        /// by the <em>writer</em> path when a mutation could not be buffered, which has no lease and
        /// no way to obtain one on the message hot path. It is safe because it only ever moves a
        /// group back to <see cref="GroupMigrationState.Redis"/> - the state that costs nothing and
        /// that an interrupted cutover would reach anyway.
        /// </para>
        /// </summary>
        internal static async Task AbandonAsync(PostgresDatabase database, long telegramChatId,
            string reason, CancellationToken cancellationToken = default)
        {
            ApplyLocal(telegramChatId, GroupMigrationState.Redis);
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE migration_group_state
   SET state = 'Redis', last_error = @Reason, lease_owner = NULL, lease_expires_at = NULL,
       chunk_cursor = 0, updated_at = now()
 WHERE telegram_chat_id = @ChatId AND state <> 'Postgres'",
                new { ChatId = telegramChatId, Reason = reason },
                cancellationToken: cancellationToken));
            await PublishAsync(telegramChatId, GroupMigrationState.Redis);
        }

        /// <summary>
        /// Abandonment from a call site that has no database handle, which is every runtime writer.
        /// </summary>
        internal static async Task AbandonAsync(long telegramChatId, string reason)
        {
            var connectionString = Helpers.RegHelper.GetRegValue("PostgresConnection");
            if (string.IsNullOrWhiteSpace(connectionString)) return;
            await AbandonAsync(PostgresDatabase.Shared(connectionString), telegramChatId, reason);
        }

        /// <summary>
        /// Deliberately returns a group to Redis, including one that has already cut over.
        /// <para>
        /// Distinct from <see cref="AbandonAsync"/>, and the distinction matters. Abandonment is
        /// raised by the writer path, holds no lease, and is only safe because it can exclusively
        /// move a group <em>back from an in-flight state</em> - un-flipping a live group underneath
        /// its readers would lose writes. A rollback is the opposite: an operator action that
        /// specifically intends to un-flip, so it takes the lease first and is allowed to.
        /// </para>
        /// <para>
        /// The caller is responsible for flushing the mirror first. Any outbox entry still pending
        /// is a PostgreSQL write Redis has not received, and flipping before it drains loses exactly
        /// those writes.
        /// </para>
        /// </summary>
        /// <returns>False when another migrator holds the lease, in which case nothing changed.</returns>
        internal static async Task<bool> RollBackAsync(PostgresDatabase database, long telegramChatId,
            string owner, string reason, CancellationToken cancellationToken = default)
        {
            var lease = await TryAcquireLeaseAsync(database, telegramChatId, owner,
                TimeSpan.FromMinutes(5), cancellationToken);
            if (lease == null) return false;
            try
            {
                await using var connection = await database.OpenConnectionAsync(cancellationToken);
                await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE migration_group_state
   SET state = 'Redis', last_error = @Reason, chunk_cursor = 0, migrated_at = NULL,
       updated_at = now()
 WHERE telegram_chat_id = @ChatId",
                    new { ChatId = telegramChatId, Reason = reason },
                    cancellationToken: cancellationToken));
                ApplyLocal(telegramChatId, GroupMigrationState.Redis);
                await PublishAsync(telegramChatId, GroupMigrationState.Redis);
                return true;
            }
            finally
            {
                await ReleaseLeaseAsync(database, lease, CancellationToken.None);
            }
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
