using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Helpers;
using Npgsql;
using NpgsqlTypes;
using StackExchange.Redis;

namespace Enforcer5.Storage.Loading
{
    internal sealed record KeyspaceScanResult(long GenerationId, long KeyCount, long UnclassifiedCount,
        bool Completed);

    /// <summary>
    /// Streams the whole Redis keyspace once and records every key, classified, in PostgreSQL.
    /// <para>
    /// One pass, not one per prefix. <c>SCAN MATCH</c> filters <em>after</em> walking the keyspace,
    /// so a per-prefix scan costs the same as a full one and N prefixes cost N times as much. The
    /// operations domain currently pays that six times per run.
    /// </para>
    /// <para>
    /// The result is a <em>superset</em>. <c>SCAN</c> guarantees a key present at both the start and
    /// the end of iteration is returned at least once, but a key deleted midway may still be
    /// reported. Consumers must verify existence rather than trust the inventory, and the
    /// deliberate consequence is that a stale entry is harmless while a missing one is not.
    /// </para>
    /// </summary>
    internal sealed class KeyspaceInventoryScanner
    {
        /// <summary>
        /// Keys buffered before a flush. The flush is what makes the scan resumable, so this trades
        /// re-scan cost after a crash against round trips: at 19.4M keys a 100k batch is ~194
        /// flushes, and losing at most 100k keys of progress is cheap to redo.
        /// </summary>
        private const int FlushSize = 100_000;

        /// <summary>
        /// <c>COUNT</c> per <c>SCAN</c> call. The cost of a pass is round trips rather than per-key
        /// work, so this is deliberately large - the measured 19.4M-key pass is dominated by
        /// pipeline throughput, not Redis CPU.
        /// </summary>
        private const int ScanPageSize = 5_000;

        private readonly PostgresDatabase _database;
        private readonly IDatabase _redis;

        internal KeyspaceInventoryScanner(PostgresDatabase database, IDatabase redis)
        {
            _database = database;
            _redis = redis;
        }

        /// <summary>
        /// Runs a full pass, resuming an unfinished generation when one exists.
        /// </summary>
        internal async Task<KeyspaceScanResult> ScanAsync(bool resume = true,
            CancellationToken cancellationToken = default)
        {
            var generation = resume ? await FindResumableGenerationAsync(cancellationToken) : null;
            var cursor = generation?.Cursor ?? "0";
            var keyCount = generation?.KeyCount ?? 0;
            var unclassified = generation?.UnclassifiedCount ?? 0;
            var generationId = generation?.GenerationId ?? await StartGenerationAsync(cancellationToken);

            if (generation != null)
                LogHelper.Info($"Resuming keyspace scan generation {generationId} at cursor {cursor} " +
                               $"({keyCount:N0} keys already recorded).");

            await EnsureStagingAsync(cancellationToken);
            var buffer = new List<(string Key, ClassifiedKey Classified)>(FlushSize);

            try
            {
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var page = (RedisResult[])await _redis.ExecuteAsync(
                        "SCAN", cursor, "MATCH", "*", "COUNT", ScanPageSize);
                    if (page.Length != 2)
                        throw new InvalidOperationException("Redis returned a malformed SCAN response.");

                    cursor = page[0].ToString();
                    foreach (var entry in (RedisResult[])page[1])
                    {
                        var key = entry.ToString();
                        var classified = MigrationKeyClassifier.Classify(key);
                        if (classified.IsUnclassified) unclassified++;
                        buffer.Add((key, classified));
                        keyCount++;
                    }

                    if (buffer.Count < FlushSize) continue;
                    await FlushAsync(generationId, buffer, cursor, keyCount, unclassified, cancellationToken);
                    buffer.Clear();
                } while (!string.Equals(cursor, "0", StringComparison.Ordinal));

                await FlushAsync(generationId, buffer, "0", keyCount, unclassified, cancellationToken);
                await CompleteGenerationAsync(generationId, keyCount, unclassified, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The cursor and counts were persisted at the last flush, so a resumed run picks up
                // there. The generation stays 'running' deliberately - it is not usable until a
                // pass finishes, and Stage C's freshness rule reads completed_at.
                LogHelper.Info($"Keyspace scan generation {generationId} cancelled at cursor {cursor}.");
                return new KeyspaceScanResult(generationId, keyCount, unclassified, false);
            }
            catch (Exception e)
            {
                await FailGenerationAsync(generationId, e, cancellationToken);
                throw;
            }

            return new KeyspaceScanResult(generationId, keyCount, unclassified, true);
        }

        /// <summary>
        /// The newest finished generation, or null when none has ever completed. This is the value
        /// Stage C's freshness rule compares a group's drain start against.
        /// </summary>
        internal static async Task<(long GenerationId, DateTimeOffset CompletedAt)?> LatestCompletedAsync(
            PostgresDatabase database, CancellationToken cancellationToken = default)
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            var row = await connection.QuerySingleOrDefaultAsync<(long GenerationId, DateTimeOffset CompletedAt)?>(
                new CommandDefinition(@"
SELECT generation_id, completed_at FROM migration_scan_generations
 WHERE status = 'completed' ORDER BY completed_at DESC LIMIT 1",
                    cancellationToken: cancellationToken));
            return row;
        }

        private async Task<GenerationState> FindResumableGenerationAsync(CancellationToken cancellationToken)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            return await connection.QuerySingleOrDefaultAsync<GenerationState>(new CommandDefinition(@"
SELECT generation_id AS GenerationId, cursor AS Cursor, key_count AS KeyCount,
       unclassified_count AS UnclassifiedCount
  FROM migration_scan_generations
 WHERE status = 'running' AND cursor IS NOT NULL
 ORDER BY started_at DESC LIMIT 1", cancellationToken: cancellationToken));
        }

        private async Task<long> StartGenerationAsync(CancellationToken cancellationToken)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            return await connection.ExecuteScalarAsync<long>(new CommandDefinition(
                "INSERT INTO migration_scan_generations (status) VALUES ('running') RETURNING generation_id",
                cancellationToken: cancellationToken));
        }

        /// <summary>
        /// Unlogged and index-free: staging is written by <c>COPY</c> and read exactly once.
        /// </summary>
        private async Task EnsureStagingAsync(CancellationToken cancellationToken)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
CREATE UNLOGGED TABLE IF NOT EXISTS migration_stage_key_inventory (
    generation_id    bigint NOT NULL,
    redis_key        text   NOT NULL,
    key_class        text   NOT NULL,
    telegram_chat_id bigint,
    telegram_user_id bigint
)", cancellationToken: cancellationToken));
        }

        /// <summary>
        /// Writes one batch and advances the persisted cursor in the same transaction, so the
        /// recorded resume point can never be ahead of the keys actually stored.
        /// </summary>
        private async Task FlushAsync(long generationId,
            List<(string Key, ClassifiedKey Classified)> buffer, string cursor, long keyCount,
            long unclassified, CancellationToken cancellationToken)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            await using (var truncate = new NpgsqlCommand(
                             "TRUNCATE migration_stage_key_inventory", connection, transaction))
                await truncate.ExecuteNonQueryAsync(cancellationToken);

            if (buffer.Count > 0)
            {
                await using (var writer = await connection.BeginBinaryImportAsync(
                                 "COPY migration_stage_key_inventory (generation_id, redis_key, " +
                                 "key_class, telegram_chat_id, telegram_user_id) FROM STDIN (FORMAT BINARY)",
                                 cancellationToken))
                {
                    foreach (var (key, classified) in buffer)
                    {
                        await writer.StartRowAsync(cancellationToken);
                        await writer.WriteAsync(generationId, NpgsqlDbType.Bigint, cancellationToken);
                        await writer.WriteAsync(key, NpgsqlDbType.Text, cancellationToken);
                        await writer.WriteAsync(classified.KeyClass, NpgsqlDbType.Text, cancellationToken);
                        await WriteNullableAsync(writer, classified.TelegramChatId, cancellationToken);
                        await WriteNullableAsync(writer, classified.TelegramUserId, cancellationToken);
                    }
                    await writer.CompleteAsync(cancellationToken);
                }

                // DISTINCT ON plus DO NOTHING: SCAN can return a key twice within one page and again
                // across a resume boundary, and neither is an error.
                await using var merge = new NpgsqlCommand(@"
INSERT INTO migration_key_inventory (generation_id, redis_key, key_class, telegram_chat_id, telegram_user_id)
SELECT DISTINCT ON (generation_id, redis_key)
       generation_id, redis_key, key_class, telegram_chat_id, telegram_user_id
  FROM migration_stage_key_inventory
 ORDER BY generation_id, redis_key
ON CONFLICT (generation_id, redis_key) DO NOTHING", connection, transaction);
                merge.CommandTimeout = 0;
                await merge.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var progress = new NpgsqlCommand(@"
UPDATE migration_scan_generations
   SET cursor = @cursor, key_count = @keyCount, unclassified_count = @unclassified
 WHERE generation_id = @generationId", connection, transaction))
            {
                progress.Parameters.AddWithValue("cursor", cursor);
                progress.Parameters.AddWithValue("keyCount", keyCount);
                progress.Parameters.AddWithValue("unclassified", unclassified);
                progress.Parameters.AddWithValue("generationId", generationId);
                await progress.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }

        private static Task WriteNullableAsync(NpgsqlBinaryImporter writer, long? value,
            CancellationToken cancellationToken) =>
            value.HasValue
                ? writer.WriteAsync(value.Value, NpgsqlDbType.Bigint, cancellationToken)
                : writer.WriteNullAsync(cancellationToken);

        private async Task CompleteGenerationAsync(long generationId, long keyCount, long unclassified,
            CancellationToken cancellationToken)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE migration_scan_generations
   SET status = 'completed', completed_at = now(), cursor = NULL,
       key_count = @KeyCount, unclassified_count = @Unclassified
 WHERE generation_id = @GenerationId",
                new { GenerationId = generationId, KeyCount = keyCount, Unclassified = unclassified },
                cancellationToken: cancellationToken));

            LogHelper.Info($"Keyspace scan generation {generationId} complete: {keyCount:N0} keys, " +
                           $"{unclassified:N0} unclassified.");
        }

        private async Task FailGenerationAsync(long generationId, Exception error,
            CancellationToken cancellationToken)
        {
            try
            {
                await using var connection = await _database.OpenConnectionAsync(cancellationToken);
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE migration_scan_generations SET status = 'failed', last_error = @Error " +
                    "WHERE generation_id = @GenerationId",
                    new { GenerationId = generationId, Error = error.Message },
                    cancellationToken: cancellationToken));
            }
            catch (Exception e)
            {
                LogHelper.Error($"Recording keyspace scan failure failed: {e.Message}");
            }
        }

        private sealed class GenerationState
        {
            public long GenerationId { get; set; }
            public string Cursor { get; set; }
            public long KeyCount { get; set; }
            public long UnclassifiedCount { get; set; }
        }
    }
}
