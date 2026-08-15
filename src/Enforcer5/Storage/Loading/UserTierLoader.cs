using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Helpers;
using Npgsql;
using NpgsqlTypes;
using StackExchange.Redis;

namespace Enforcer5.Storage.Loading
{
    internal sealed record UserTierLoadResult(long UsersStaged, long GroupCountersStaged,
        long AccountsCreated, long GroupsCreated, long MissingNames);

    /// <summary>
    /// Loads the user and global tier from Redis into PostgreSQL in bulk.
    /// <para>
    /// Stage B migrates this tier to completeness before any group cuts over, and it is deliberately
    /// not lazy. For <c>globalBan:*</c> and <c>ban:*</c> absence means <em>allowed</em>, so a lazy
    /// miss is a silent security false-negative rather than a slow path; for the username index a
    /// miss creates a duplicate identity. Completeness is a correctness requirement here, not an
    /// optimisation.
    /// </para>
    /// <para>
    /// The shape is Redis to binary <c>COPY</c> to unlogged staging, then a single set-based merge.
    /// One Telegram user becomes five canonical rows - users, telegram_accounts,
    /// user_identity_links, telegram_account_profiles and account_message_totals - so 5.1M users is
    /// roughly 25M rows, and nothing row-at-a-time survives contact with that.
    /// </para>
    /// </summary>
    internal sealed class UserTierLoader
    {
        /// <summary>
        /// Measured knee for Redis hydration. Depth 100 is 5.4x slower; 25k and 50k give nothing
        /// back. StackExchange.Redis multiplexes one connection, so this controls how many commands
        /// are queued before the batch is awaited, not how many sockets are used.
        /// </summary>
        private const int PipelineDepth = 10_000;

        /// <summary>
        /// Users per staging transaction. This is the resume granularity: a crash costs at most one
        /// chunk of re-read, and staging itself is the checkpoint.
        /// </summary>
        private const int ChunkSize = 250_000;

        private static readonly RedisValue[] UserFields = ["name", "username", "msgs"];

        private readonly PostgresDatabase _database;
        private readonly IDatabase _redis;

        internal UserTierLoader(PostgresDatabase database, IDatabase redis)
        {
            _database = database;
            _redis = redis;
        }

        internal async Task<UserTierLoadResult> LoadAsync(long generationId, MigrationRunTracker tracker,
            CancellationToken cancellationToken = default)
        {
            await EnsureStagingAsync(cancellationToken);

            var users = await CopyUsersAsync(generationId, cancellationToken);
            var groupCounters = await CopyGroupStatisticsAsync(generationId, cancellationToken);
            var (accounts, groups, missingNames) = await MergeAsync(tracker, cancellationToken);

            LogHelper.Info($"User tier load: staged={users:N0} users, {groupCounters:N0} group counters; " +
                           $"created={accounts:N0} accounts, {groups:N0} groups.");
            return new UserTierLoadResult(users, groupCounters, accounts, groups, missingNames);
        }

        /// <summary>
        /// Unlogged and index-free. Staging is written once by <c>COPY</c> and read once by the
        /// merge, so durability buys nothing and indexes only slow the write.
        /// </summary>
        private async Task EnsureStagingAsync(CancellationToken cancellationToken)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
CREATE UNLOGGED TABLE IF NOT EXISTS migration_stage_users (
    telegram_user_id bigint NOT NULL,
    display_name     text,
    username         text,
    global_messages  bigint,
    legacy_messages  bigint
);
CREATE UNLOGGED TABLE IF NOT EXISTS migration_stage_group_members (
    telegram_chat_id bigint NOT NULL,
    telegram_user_id bigint NOT NULL,
    messages         bigint
);", cancellationToken: cancellationToken));
        }

        /// <summary>
        /// Streams every user id the inventory saw and hydrates it from Redis into staging.
        /// <para>
        /// The population is the union of <c>user:{id}</c> and <c>chat:{userId}</c>, not just the
        /// former. 34,736 users exist only as a message counter with no user record, all with
        /// non-zero counts, and seeding from <c>user:*</c> alone would drop them silently.
        /// </para>
        /// </summary>
        private async Task<long> CopyUsersAsync(long generationId, CancellationToken cancellationToken)
        {
            // Staging is its own watermark: resume from the highest id already written rather than
            // tracking a separate cursor that could disagree with the data.
            var after = await ScalarAsync<long?>(
                "SELECT max(telegram_user_id) FROM migration_stage_users", cancellationToken) ?? long.MinValue;
            var total = 0L;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var ids = await ReadIdsAsync(@"
SELECT DISTINCT telegram_user_id FROM migration_key_inventory
 WHERE generation_id = @Generation AND telegram_user_id IS NOT NULL
   AND key_class = ANY(@Classes) AND telegram_user_id > @After
 ORDER BY telegram_user_id LIMIT @Limit",
                    new
                    {
                        Generation = generationId,
                        Classes = new[] { MigrationKeyClasses.User, MigrationKeyClasses.UserMessageTotal },
                        After = after,
                        Limit = ChunkSize
                    }, cancellationToken);
                if (ids.Count == 0) break;

                total += await CopyUserChunkAsync(ids, cancellationToken);
                after = ids[^1];
            }

            return total;
        }

        private async Task<long> CopyUserChunkAsync(IReadOnlyList<long> ids, CancellationToken cancellationToken)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var writer = await connection.BeginBinaryImportAsync(
                "COPY migration_stage_users (telegram_user_id, display_name, username, " +
                "global_messages, legacy_messages) FROM STDIN (FORMAT BINARY)", cancellationToken);

            var profiles = new Task<RedisValue[]>[PipelineDepth];
            var totals = new Task<RedisValue>[PipelineDepth];
            var batchIds = new long[PipelineDepth];
            var pending = 0;
            var written = 0L;

            foreach (var id in ids)
            {
                batchIds[pending] = id;
                profiles[pending] = _redis.HashGetAsync($"user:{id}", UserFields);
                // Read the lifetime total alongside the profile rather than materialising all 4.75M
                // counters into a dictionary first. The dictionary was several hundred MB, on a host
                // with roughly 1.6 GB free.
                totals[pending] = _redis.HashGetAsync($"chat:{id}", "msgs");
                if (++pending != PipelineDepth) continue;
                written += await DrainUsersAsync(writer, profiles, totals, batchIds, pending, cancellationToken);
                pending = 0;
            }
            if (pending > 0)
                written += await DrainUsersAsync(writer, profiles, totals, batchIds, pending, cancellationToken);

            await writer.CompleteAsync(cancellationToken);
            return written;
        }

        private static async Task<long> DrainUsersAsync(NpgsqlBinaryImporter writer,
            Task<RedisValue[]>[] profiles, Task<RedisValue>[] totals, long[] ids, int count,
            CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                var values = await profiles[i];
                var total = await totals[i];

                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(ids[i], NpgsqlDbType.Bigint, cancellationToken);
                await WriteTextAsync(writer, values[0].HasValue ? values[0].ToString() : null, cancellationToken);
                await WriteTextAsync(writer, values[1].HasValue ? values[1].ToString() : null, cancellationToken);
                await WriteCountAsync(writer, total, cancellationToken);
                // user:{id}.msgs is the pre-schema-change predecessor of chat:{userId}.msgs. It is
                // carried separately and never summed, because transitional records hold both.
                await WriteCountAsync(writer, values[2], cancellationToken);
            }
            return count;
        }

        /// <summary>
        /// Streams the per-group counters into staging, reading each key's value.
        /// <para>
        /// The values are what make the unattributed residual computable: the user's global counter
        /// is authoritative and is never below the sum of the group counters, so the difference is
        /// history no surviving group accounts for.
        /// </para>
        /// </summary>
        private async Task<long> CopyGroupStatisticsAsync(long generationId, CancellationToken cancellationToken)
        {
            var afterChat = await ScalarAsync<long?>(
                "SELECT max(telegram_chat_id) FROM migration_stage_group_members", cancellationToken);
            var afterUser = afterChat == null
                ? long.MinValue
                : await ScalarAsync<long?>(
                    "SELECT max(telegram_user_id) FROM migration_stage_group_members " +
                    "WHERE telegram_chat_id = @Chat", cancellationToken, new { Chat = afterChat })
                  ?? long.MinValue;
            var total = 0L;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pairs = (await QueryAsync<(long ChatId, long UserId)>(@"
SELECT DISTINCT telegram_chat_id AS ChatId, telegram_user_id AS UserId
  FROM migration_key_inventory
 WHERE generation_id = @Generation AND key_class = @Class
   AND telegram_chat_id IS NOT NULL AND telegram_user_id IS NOT NULL
   AND (telegram_chat_id, telegram_user_id) > (@AfterChat, @AfterUser)
 ORDER BY telegram_chat_id, telegram_user_id LIMIT @Limit",
                    new
                    {
                        Generation = generationId,
                        Class = MigrationKeyClasses.GroupStatistics,
                        AfterChat = afterChat ?? long.MinValue,
                        AfterUser = afterUser,
                        Limit = ChunkSize
                    }, cancellationToken)).ToList();
                if (pairs.Count == 0) break;

                total += await CopyGroupChunkAsync(pairs, cancellationToken);
                (afterChat, afterUser) = pairs[^1];
            }

            return total;
        }

        private async Task<long> CopyGroupChunkAsync(IReadOnlyList<(long ChatId, long UserId)> pairs,
            CancellationToken cancellationToken)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var writer = await connection.BeginBinaryImportAsync(
                "COPY migration_stage_group_members (telegram_chat_id, telegram_user_id, messages) " +
                "FROM STDIN (FORMAT BINARY)", cancellationToken);

            var batch = new Task<RedisValue>[PipelineDepth];
            var batchPairs = new (long ChatId, long UserId)[PipelineDepth];
            var pending = 0;
            var written = 0L;

            foreach (var pair in pairs)
            {
                batchPairs[pending] = pair;
                batch[pending] = _redis.HashGetAsync($"{pair.ChatId}:users:{pair.UserId}", "msgs");
                if (++pending != PipelineDepth) continue;
                written += await DrainGroupsAsync(writer, batch, batchPairs, pending, cancellationToken);
                pending = 0;
            }
            if (pending > 0)
                written += await DrainGroupsAsync(writer, batch, batchPairs, pending, cancellationToken);

            await writer.CompleteAsync(cancellationToken);
            return written;
        }

        private static async Task<long> DrainGroupsAsync(NpgsqlBinaryImporter writer,
            Task<RedisValue>[] batch, (long ChatId, long UserId)[] pairs, int count,
            CancellationToken cancellationToken)
        {
            for (var i = 0; i < count; i++)
            {
                var value = await batch[i];
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(pairs[i].ChatId, NpgsqlDbType.Bigint, cancellationToken);
                await writer.WriteAsync(pairs[i].UserId, NpgsqlDbType.Bigint, cancellationToken);
                await WriteCountAsync(writer, value, cancellationToken);
            }
            return count;
        }

        /// <summary>
        /// Staging to canonical, in one transaction with the constraints dropped.
        /// <para>
        /// One transaction because the constraint drop has to roll back with the load if anything
        /// fails - see <see cref="ConstraintManager"/>. Everything inside is set-based, so the cost
        /// is the index rebuild rather than the row count.
        /// </para>
        /// </summary>
        private async Task<(long Accounts, long Groups, long MissingNames)> MergeAsync(
            MigrationRunTracker tracker, CancellationToken cancellationToken)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var dropped = await ConstraintManager.DropAsync(connection, transaction, UserTierTables,
                cancellationToken);

            var accounts = await BulkPrincipalResolver.EnsureAccountsAsync(connection, transaction,
                "SELECT telegram_user_id FROM migration_stage_users", cancellationToken);
            var groups = await BulkPrincipalResolver.EnsureGroupsAsync(connection, transaction,
                "SELECT telegram_chat_id FROM migration_stage_group_members", cancellationToken);

            await ExecuteAsync(connection, transaction, @"
INSERT INTO telegram_account_profiles (account_id, display_name, username, valid_from)
SELECT a.account_id, s.display_name, s.username, now()
  FROM migration_stage_users s
  JOIN telegram_accounts a ON a.telegram_user_id = s.telegram_user_id
 WHERE (s.display_name IS NOT NULL OR s.username IS NOT NULL)
   AND NOT EXISTS (SELECT 1 FROM telegram_account_profiles p
                    WHERE p.account_id = a.account_id AND p.valid_to IS NULL);

INSERT INTO account_message_totals (account_id, message_count, unattributed_count, captured_at)
SELECT a.account_id, s.global_messages,
       -- The global counter is authoritative and is never lower than the sum of the user's
       -- per-group counters; the residual is history no surviving group accounts for. Storing it
       -- explicitly keeps message_count = SUM(group totals) + unattributed_count exact, and
       -- recomputing the total from group sums instead would lose 27.5M messages and zero 34,736
       -- users outright.
       GREATEST(s.global_messages - COALESCE(g.group_total, 0), 0), now()
  FROM migration_stage_users s
  JOIN telegram_accounts a ON a.telegram_user_id = s.telegram_user_id
  LEFT JOIN (SELECT telegram_user_id, SUM(messages) AS group_total
               FROM migration_stage_group_members GROUP BY telegram_user_id) g
         ON g.telegram_user_id = s.telegram_user_id
 WHERE s.global_messages IS NOT NULL
ON CONFLICT (account_id) DO NOTHING;

-- The group tier. The prototype loaded these counters only to sum them for the residual and never
-- wrote them, so every per-group total would have arrived at zero.
INSERT INTO group_account_message_totals (group_id, account_id, message_count, captured_at)
SELECT gr.group_id, a.account_id, m.messages, now()
  FROM migration_stage_group_members m
  JOIN telegram_accounts a ON a.telegram_user_id = m.telegram_user_id
  JOIN telegram_chats c ON c.telegram_chat_id = m.telegram_chat_id
  JOIN groups gr ON gr.chat_id = c.chat_id
 WHERE m.messages IS NOT NULL
ON CONFLICT (group_id, account_id) DO NOTHING;", cancellationToken);

            var missingNames = await RecordMissingNamesAsync(connection, transaction, tracker,
                cancellationToken);

            await ConstraintManager.RebuildAsync(connection, transaction, dropped, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return (accounts, groups, missingNames);
        }

        /// <summary>
        /// Telegram always supplies <c>first_name</c>, so a record without one is a defective write
        /// rather than a user to discard. Discarding them is exactly the defect that lost 326,787
        /// users - 6.38% of the estate - so the user is migrated and the defect recorded. A missing
        /// username is deliberately not flagged: 40.6% of users legitimately have none.
        /// </summary>
        private static async Task<long> RecordMissingNamesAsync(NpgsqlConnection connection,
            NpgsqlTransaction transaction, MigrationRunTracker tracker, CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand(@"
INSERT INTO migration_exceptions
    (migration_run_id, domain, source_key, source_field, severity, reason, created_at)
SELECT @runId, 'principal-content', 'user:' || telegram_user_id, 'name', 'warning',
       'Record has no name. Telegram always supplies first_name, so this is a defective write; '
       || 'the user is migrated and the missing name recorded.', now()
  FROM migration_stage_users
 WHERE display_name IS NULL", connection, transaction);
            command.CommandTimeout = 0;
            command.Parameters.AddWithValue("runId", tracker.RunId);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        private static readonly string[] UserTierTables =
        [
            "users", "telegram_accounts", "user_identity_links", "telegram_account_profiles",
            "account_message_totals", "group_account_message_totals"
        ];

        private static Task WriteTextAsync(NpgsqlBinaryImporter writer, string value,
            CancellationToken cancellationToken) =>
            value is null
                ? writer.WriteNullAsync(cancellationToken)
                : writer.WriteAsync(value, NpgsqlDbType.Text, cancellationToken);

        /// <summary>
        /// A counter that is absent, unparseable or negative is written as null rather than zero.
        /// Zero is a real value that would silently overwrite a genuine total; null means unknown.
        /// </summary>
        private static Task WriteCountAsync(NpgsqlBinaryImporter writer, RedisValue value,
            CancellationToken cancellationToken) =>
            value.HasValue && long.TryParse(value.ToString(), out var parsed) && parsed >= 0
                ? writer.WriteAsync(parsed, NpgsqlDbType.Bigint, cancellationToken)
                : writer.WriteNullAsync(cancellationToken);

        private async Task<List<long>> ReadIdsAsync(string sql, object parameters,
            CancellationToken cancellationToken)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            return (await connection.QueryAsync<long>(new CommandDefinition(sql, parameters,
                cancellationToken: cancellationToken))).AsList();
        }

        private async Task<IEnumerable<T>> QueryAsync<T>(string sql, object parameters,
            CancellationToken cancellationToken)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            return await connection.QueryAsync<T>(new CommandDefinition(sql, parameters,
                cancellationToken: cancellationToken));
        }

        private async Task<T> ScalarAsync<T>(string sql, CancellationToken cancellationToken,
            object parameters = null)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            return await connection.ExecuteScalarAsync<T>(new CommandDefinition(sql, parameters,
                cancellationToken: cancellationToken));
        }

        private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
            string sql, CancellationToken cancellationToken)
        {
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            command.CommandTimeout = 0;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}
