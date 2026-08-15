using Npgsql;
using NpgsqlTypes;
using StackExchange.Redis;

namespace Enforcer5.Benchmarks;

/// <summary>
/// Benchmarks 2-4 - the proposed user-tier loader.
/// <para>
/// This is a prototype of the design, not a wrapper over the existing repositories. Three things
/// are being proven: that Redis can be streamed straight into a binary COPY, that identities can
/// be assigned in bulk instead of round-tripping <c>GENERATED ALWAYS AS IDENTITY</c> values, and
/// how much of the total is index and foreign-key maintenance.
/// </para>
/// <para>
/// One Telegram user becomes five canonical rows - <c>users</c>, <c>telegram_accounts</c>,
/// <c>user_identity_links</c>, <c>telegram_account_profiles</c> and <c>account_message_totals</c> -
/// so 5.1M users is roughly 25M rows.
/// </para>
/// </summary>
internal sealed class UserTierLoadBenchmark
{
    private static readonly RedisValue[] UserFields = { "name", "username", "msgs" };

    private readonly IDatabase _redis;
    private readonly string _connectionString;
    private readonly ResultRecorder _recorder;

    internal UserTierLoadBenchmark(IDatabase redis, string connectionString, ResultRecorder recorder)
    {
        _redis = redis;
        _connectionString = connectionString;
        _recorder = recorder;
    }

    internal async Task RunAsync(KeyspaceInventory inventory, IReadOnlyCollection<string> selected,
        int pipelineDepth, CancellationToken cancellationToken)
    {
        await using var dataSource = new NpgsqlDataSourceBuilder(_connectionString).Build();

        if (selected.Contains("2"))
            await RunCopyAsync(dataSource, inventory, pipelineDepth, cancellationToken);

        // 3 and 4 are two halves of the same question, so they share one loaded staging table.
        if (selected.Contains("3") || selected.Contains("4"))
            await RunMergeAsync(dataSource, selected, cancellationToken);
    }

    // ---------------------------------------------------------------- benchmark 2

    private async Task RunCopyAsync(NpgsqlDataSource dataSource, KeyspaceInventory inventory,
        int pipelineDepth, CancellationToken cancellationToken)
    {
        Console.WriteLine("\nBenchmark 2 - binary COPY into staging");

        await using (var connection = await dataSource.OpenConnectionAsync(cancellationToken))
        {
            await ExecuteAsync(connection, StagingDdl, cancellationToken);
        }

        var counters = await LoadGlobalCountersAsync(inventory, pipelineDepth, cancellationToken);

        var rows = 0L;
        using (_recorder.Measure("copy-users", $"binary COPY, depth {pipelineDepth}", () => rows,
                   new Dictionary<string, string> { ["depth"] = pipelineDepth.ToString() }))
        {
            rows = await CopyUsersAsync(dataSource, inventory, counters, pipelineDepth, cancellationToken);
        }

        var statRows = 0L;
        using (_recorder.Measure("copy-group-stats", $"read msgs + binary COPY, depth {pipelineDepth}", () => statRows))
        {
            statRows = await CopyGroupStatisticsAsync(dataSource, inventory, pipelineDepth, cancellationToken);
        }
    }

    /// <summary>
    /// Reads every <c>chat:{userId}.msgs</c> - the user's global lifetime total - into memory.
    /// At roughly 4.75M entries this is a few hundred MB and lets the user COPY be a single pass.
    /// </summary>
    private async Task<Dictionary<long, long>> LoadGlobalCountersAsync(KeyspaceInventory inventory,
        int pipelineDepth, CancellationToken cancellationToken)
    {
        var counters = new Dictionary<long, long>(inventory.GlobalMessageCounterIds.Count);
        var batchIds = new long[pipelineDepth];
        var batch = new Task<RedisValue>[pipelineDepth];
        var pending = 0;

        using (_recorder.Measure("read-global-counters", $"HGET msgs, depth {pipelineDepth}",
                   () => counters.Count))
        {
            foreach (var id in inventory.GlobalMessageCounterIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                batchIds[pending] = id;
                batch[pending] = _redis.HashGetAsync($"chat:{id}", "msgs");
                if (++pending != pipelineDepth) continue;
                await DrainCountersAsync(batch, batchIds, pending, counters);
                pending = 0;
            }
            if (pending > 0) await DrainCountersAsync(batch, batchIds, pending, counters);
        }

        return counters;
    }

    private static async Task DrainCountersAsync(Task<RedisValue>[] batch, long[] ids, int count,
        Dictionary<long, long> counters)
    {
        for (var i = 0; i < count; i++)
        {
            var value = await batch[i];
            if (value.HasValue && long.TryParse(value.ToString(), out var messages) && messages >= 0)
                counters[ids[i]] = messages;
        }
    }

    private async Task<long> CopyUsersAsync(NpgsqlDataSource dataSource, KeyspaceInventory inventory,
        Dictionary<long, long> counters, int pipelineDepth, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY bench_stage_users (telegram_user_id, display_name, username, global_messages, " +
            "legacy_messages) FROM STDIN (FORMAT BINARY)", cancellationToken);

        var rows = 0L;
        var batchIds = new long[pipelineDepth];
        var batch = new Task<RedisValue[]>[pipelineDepth];
        var pending = 0;

        foreach (var id in inventory.UserIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            batchIds[pending] = id;
            batch[pending] = _redis.HashGetAsync($"user:{id}", UserFields);
            if (++pending != pipelineDepth) continue;
            rows += await WriteBatchAsync(writer, batch, batchIds, pending, counters, cancellationToken);
            pending = 0;
        }
        if (pending > 0)
            rows += await WriteBatchAsync(writer, batch, batchIds, pending, counters, cancellationToken);

        await writer.CompleteAsync(cancellationToken);
        return rows;
    }

    private static async Task<long> WriteBatchAsync(NpgsqlBinaryImporter writer,
        Task<RedisValue[]>[] batch, long[] ids, int count, Dictionary<long, long> counters,
        CancellationToken cancellationToken)
    {
        var written = 0L;
        for (var i = 0; i < count; i++)
        {
            var values = await batch[i];
            var id = ids[i];

            var name = values[0].HasValue ? values[0].ToString() : null;
            var username = values[1].HasValue ? values[1].ToString() : null;
            // user:{id}.msgs is the pre-schema-change predecessor of chat:{userId}.msgs. It is
            // carried separately and never summed, because transitional records hold both.
            long? legacyMessages = values[2].HasValue &&
                                   long.TryParse(values[2].ToString(), out var legacy) && legacy >= 0
                ? legacy
                : null;

            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(id, NpgsqlDbType.Bigint, cancellationToken);
            await WriteNullableTextAsync(writer, name, cancellationToken);
            await WriteNullableTextAsync(writer, username, cancellationToken);
            if (counters.TryGetValue(id, out var global))
                await writer.WriteAsync(global, NpgsqlDbType.Bigint, cancellationToken);
            else
                await writer.WriteNullAsync(cancellationToken);
            if (legacyMessages.HasValue)
                await writer.WriteAsync(legacyMessages.Value, NpgsqlDbType.Bigint, cancellationToken);
            else
                await writer.WriteNullAsync(cancellationToken);

            written++;
        }
        return written;
    }

    private static Task WriteNullableTextAsync(NpgsqlBinaryImporter writer, string value,
        CancellationToken cancellationToken) =>
        value is null
            ? writer.WriteNullAsync(cancellationToken)
            : writer.WriteAsync(value, NpgsqlDbType.Text, cancellationToken);

    /// <summary>
    /// Streams the per-group statistics into staging, reading each key's <c>msgs</c> value rather
    /// than only the id pair. The values are needed to compute the unattributed residual on
    /// <c>account_message_totals</c>, and reading them is also the measurement that was previously
    /// missing from this benchmark.
    /// </summary>
    private async Task<long> CopyGroupStatisticsAsync(NpgsqlDataSource dataSource,
        KeyspaceInventory inventory, int pipelineDepth, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var writer = await connection.BeginBinaryImportAsync(
            "COPY bench_stage_group_members (group_id, telegram_user_id, messages) " +
            "FROM STDIN (FORMAT BINARY)", cancellationToken);

        var rows = 0L;
        var batchGroups = new long[pipelineDepth];
        var batchUsers = new long[pipelineDepth];
        var batch = new Task<RedisValue>[pipelineDepth];
        var pending = 0;

        foreach (var (groupId, members) in inventory.GroupStatisticsUsers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var memberId in members)
            {
                batchGroups[pending] = groupId;
                batchUsers[pending] = memberId;
                batch[pending] = _redis.HashGetAsync($"{groupId}:users:{memberId}", "msgs");
                if (++pending != pipelineDepth) continue;
                rows += await WriteGroupBatchAsync(writer, batch, batchGroups, batchUsers, pending,
                    cancellationToken);
                pending = 0;
            }
        }
        if (pending > 0)
            rows += await WriteGroupBatchAsync(writer, batch, batchGroups, batchUsers, pending,
                cancellationToken);

        await writer.CompleteAsync(cancellationToken);
        return rows;
    }

    private static async Task<long> WriteGroupBatchAsync(NpgsqlBinaryImporter writer,
        Task<RedisValue>[] batch, long[] groups, long[] users, int count,
        CancellationToken cancellationToken)
    {
        var written = 0L;
        for (var i = 0; i < count; i++)
        {
            var value = await batch[i];
            await writer.StartRowAsync(cancellationToken);
            await writer.WriteAsync(groups[i], NpgsqlDbType.Bigint, cancellationToken);
            await writer.WriteAsync(users[i], NpgsqlDbType.Bigint, cancellationToken);
            if (value.HasValue && long.TryParse(value.ToString(), out var messages) && messages >= 0)
                await writer.WriteAsync(messages, NpgsqlDbType.Bigint, cancellationToken);
            else
                await writer.WriteNullAsync(cancellationToken);
            written++;
        }
        return written;
    }

    // ---------------------------------------------------------------- benchmarks 3 and 4

    private async Task RunMergeAsync(NpgsqlDataSource dataSource, IReadOnlyCollection<string> selected,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("\nBenchmarks 3 and 4 - staging to canonical");

        foreach (var withConstraints in new[] { true, false })
        {
            var variant = withConstraints ? "indexes and FKs present" : "constraints dropped first";
            await ResetCanonicalAsync(dataSource, cancellationToken);

            if (!withConstraints)
                await ExecuteOnConnectionAsync(dataSource, DropConstraintsSql, cancellationToken);

            var merged = 0L;
            using (_recorder.Measure("merge-user-tier", variant, () => merged))
            {
                merged = await MergeUserTierAsync(dataSource, cancellationToken);
            }

            if (!withConstraints && selected.Contains("4"))
            {
                var rebuilt = merged;
                using (_recorder.Measure("rebuild-constraints", "indexes and FKs", () => rebuilt))
                {
                    await ExecuteOnConnectionAsync(dataSource, RebuildConstraintsSql, cancellationToken);
                }
            }

            // Measured separately so the merge figures stay comparable across variants.
            var flagged = 0L;
            using (_recorder.Measure("record-exceptions", $"missing name, {variant}", () => flagged))
            {
                flagged = await RecordMissingNamesAsync(dataSource, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Telegram always supplies <c>first_name</c>, so a legacy record without one is a defective
    /// write rather than a user that should be discarded. The record is migrated and the defect is
    /// recorded. A missing username is deliberately not flagged - 40.6% of users legitimately have
    /// none.
    /// </summary>
    private static async Task<long> RecordMissingNamesAsync(NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var runId = await ScalarAsync<long>(connection, transaction, @"
INSERT INTO migration_runs (mode, status, source_snapshot, details)
VALUES ('benchmark-user-tier', 'completed', 'rehearsal-rdb', '{}'::jsonb)
RETURNING id", cancellationToken);

        await using (var command = new NpgsqlCommand(@"
INSERT INTO migration_exceptions
    (migration_run_id, domain, source_key, source_field, severity, reason, created_at)
SELECT @runId, 'principal-content', 'user:' || telegram_user_id, 'name', 'warning',
       'Record has no name. Telegram always supplies first_name, so this is a defective write; '
       || 'the user is migrated and the missing name recorded.', now()
FROM bench_stage_users
WHERE display_name IS NULL", connection, transaction))
        {
            command.CommandTimeout = 0;
            command.Parameters.AddWithValue("runId", runId);
            var flagged = await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return flagged;
        }
    }

    /// <summary>
    /// Assigns <c>user_id</c> and <c>account_id</c> from reserved sequence blocks instead of
    /// inserting row by row to read generated identities back. Every dependent table can then be
    /// written with a set-based insert, which is what makes the whole tier a handful of statements.
    /// </summary>
    private async Task<long> MergeUserTierAsync(NpgsqlDataSource dataSource,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var pending = await ScalarAsync<long>(connection, transaction,
            "SELECT count(*) FROM bench_stage_users", cancellationToken);
        if (pending == 0) return 0;

        var userBase = await ScalarAsync<long>(connection, transaction,
            "SELECT setval(pg_get_serial_sequence('users','user_id'), " +
            "coalesce((SELECT max(user_id) FROM users), 0) + @count) - @count + 1",
            cancellationToken, ("count", pending));
        var accountBase = await ScalarAsync<long>(connection, transaction,
            "SELECT setval(pg_get_serial_sequence('telegram_accounts','account_id'), " +
            "coalesce((SELECT max(account_id) FROM telegram_accounts), 0) + @count) - @count + 1",
            cancellationToken, ("count", pending));

        await ExecuteAsync(connection, $@"
CREATE TEMP TABLE bench_assigned ON COMMIT DROP AS
SELECT s.telegram_user_id, s.display_name, s.username, s.global_messages,
       {userBase} + (row_number() OVER (ORDER BY s.telegram_user_id)) - 1 AS user_id,
       {accountBase} + (row_number() OVER (ORDER BY s.telegram_user_id)) - 1 AS account_id
FROM bench_stage_users s
WHERE NOT EXISTS (
    SELECT 1 FROM telegram_accounts a WHERE a.telegram_user_id = s.telegram_user_id);

INSERT INTO users (user_id, created_at, updated_at)
OVERRIDING SYSTEM VALUE
SELECT user_id, now(), now() FROM bench_assigned;

INSERT INTO telegram_accounts (account_id, telegram_user_id, is_bot, first_seen_at, last_seen_at)
OVERRIDING SYSTEM VALUE
SELECT account_id, telegram_user_id, false, now(), now() FROM bench_assigned;

INSERT INTO user_identity_links (user_id, account_id, source, linked_at)
SELECT user_id, account_id, 'migration', now() FROM bench_assigned;

INSERT INTO telegram_account_profiles (account_id, display_name, username, valid_from)
SELECT account_id, display_name, username, now() FROM bench_assigned
WHERE display_name IS NOT NULL OR username IS NOT NULL;

INSERT INTO account_message_totals (account_id, message_count, unattributed_count, captured_at)
SELECT a.account_id, a.global_messages,
       -- The global counter is authoritative and is never lower than the sum of the user's
       -- per-group counters; the residual is history no surviving group accounts for. Storing it
       -- explicitly keeps message_count = SUM(group totals) + unattributed_count exact.
       GREATEST(a.global_messages - COALESCE(g.group_total, 0), 0),
       now()
FROM bench_assigned a
LEFT JOIN (
    SELECT telegram_user_id, SUM(messages) AS group_total
    FROM bench_stage_group_members
    GROUP BY telegram_user_id
) g ON g.telegram_user_id = a.telegram_user_id
WHERE a.global_messages IS NOT NULL
ON CONFLICT (account_id) DO NOTHING;
", cancellationToken, transaction);

        var inserted = await ScalarAsync<long>(connection, transaction,
            "SELECT count(*) FROM bench_assigned", cancellationToken);

        await transaction.CommitAsync(cancellationToken);
        return inserted;
    }

    // ---------------------------------------------------------------- helpers

    private static async Task ResetCanonicalAsync(NpgsqlDataSource dataSource,
        CancellationToken cancellationToken) =>
        await ExecuteOnConnectionAsync(dataSource, @"
DELETE FROM migration_exceptions;
DELETE FROM migration_runs;
TRUNCATE account_message_totals, telegram_account_profiles, user_identity_links,
         telegram_accounts, users RESTART IDENTITY CASCADE;", cancellationToken);

    private static async Task ExecuteOnConnectionAsync(NpgsqlDataSource dataSource, string sql,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await ExecuteAsync(connection, sql, cancellationToken);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql,
        CancellationToken cancellationToken, NpgsqlTransaction transaction = null)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection,
        NpgsqlTransaction transaction, string sql, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.CommandTimeout = 0;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return (T)Convert.ChangeType(await command.ExecuteScalarAsync(cancellationToken), typeof(T));
    }

    private const string StagingDdl = @"
DROP TABLE IF EXISTS bench_stage_users;
DROP TABLE IF EXISTS bench_stage_group_members;

-- Unlogged and index-free: staging exists to be written fast and read once.
CREATE UNLOGGED TABLE bench_stage_users (
    telegram_user_id bigint NOT NULL,
    display_name     text,
    username         text,
    global_messages  bigint,
    legacy_messages  bigint
);

CREATE UNLOGGED TABLE bench_stage_group_members (
    group_id         bigint NOT NULL,
    telegram_user_id bigint NOT NULL,
    messages         bigint
);";

    private const string DropConstraintsSql = @"
ALTER TABLE user_identity_links       DROP CONSTRAINT IF EXISTS ""FK_user_identity_links_users_user_id"";
ALTER TABLE user_identity_links       DROP CONSTRAINT IF EXISTS ""FK_user_identity_links_telegram_accounts_account_id"";
ALTER TABLE telegram_account_profiles DROP CONSTRAINT IF EXISTS ""FK_telegram_account_profiles_telegram_accounts_account_id"";
ALTER TABLE account_message_totals    DROP CONSTRAINT IF EXISTS ""FK_account_message_totals_telegram_accounts_account_id"";
DROP INDEX IF EXISTS ""IX_telegram_accounts_telegram_user_id"";
DROP INDEX IF EXISTS telegram_account_profiles_open_idx;
DROP INDEX IF EXISTS telegram_account_profiles_username_ci_idx;
DROP INDEX IF EXISTS user_identity_links_active_account_idx;
DROP INDEX IF EXISTS ""IX_user_identity_links_user_id_account_id_linked_at"";";

    private const string RebuildConstraintsSql = @"
CREATE UNIQUE INDEX ""IX_telegram_accounts_telegram_user_id"" ON telegram_accounts (telegram_user_id);
CREATE UNIQUE INDEX telegram_account_profiles_open_idx ON telegram_account_profiles (account_id) WHERE valid_to IS NULL;
CREATE INDEX telegram_account_profiles_username_ci_idx ON telegram_account_profiles (username) WHERE username IS NOT NULL;
CREATE UNIQUE INDEX user_identity_links_active_account_idx ON user_identity_links (account_id) WHERE unlinked_at IS NULL;
CREATE INDEX ""IX_user_identity_links_user_id_account_id_linked_at"" ON user_identity_links (user_id, account_id, linked_at);
ALTER TABLE user_identity_links       ADD CONSTRAINT ""FK_user_identity_links_users_user_id"" FOREIGN KEY (user_id) REFERENCES users(user_id) ON DELETE RESTRICT;
ALTER TABLE user_identity_links       ADD CONSTRAINT ""FK_user_identity_links_telegram_accounts_account_id"" FOREIGN KEY (account_id) REFERENCES telegram_accounts(account_id) ON DELETE RESTRICT;
ALTER TABLE telegram_account_profiles ADD CONSTRAINT ""FK_telegram_account_profiles_telegram_accounts_account_id"" FOREIGN KEY (account_id) REFERENCES telegram_accounts(account_id) ON DELETE CASCADE;
ALTER TABLE account_message_totals    ADD CONSTRAINT ""FK_account_message_totals_telegram_accounts_account_id"" FOREIGN KEY (account_id) REFERENCES telegram_accounts(account_id) ON DELETE CASCADE;";
}
