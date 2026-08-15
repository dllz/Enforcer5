using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Enforcer5.Storage.Loading
{
    /// <summary>
    /// Reads populations out of a completed keyspace inventory.
    /// <para>
    /// Every caller here previously ran its own <c>SCAN</c>. An operations backfill did three
    /// (<c>user:*</c>, <c>globalBan:*</c>, <c>flagged:*</c>) and principal-content a fourth, each
    /// walking all 19.4M keys because <c>MATCH</c> filters after the walk rather than before it.
    /// One recorded pass replaces all of them, and - unlike a live scan - gives every domain the
    /// same view of the keyspace.
    /// </para>
    /// </summary>
    internal static class MigrationInventory
    {
        /// <summary>
        /// Every group the inventory saw, from any group-scoped key shape.
        /// <para>
        /// This is deliberately not <c>bot:groupsid</c>. That set is add-only and was written from
        /// one code path out of three, so it missed 7,951 groups that hold real data - the orphan
        /// population. Deriving the group list from observed keys cannot miss a group that has any.
        /// </para>
        /// </summary>
        internal static async Task<IReadOnlyCollection<long>> GroupIdsAsync(PostgresDatabase database,
            long generationId, CancellationToken cancellationToken = default)
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            return (await connection.QueryAsync<long>(new CommandDefinition(@"
SELECT DISTINCT telegram_chat_id FROM migration_key_inventory
 WHERE generation_id = @Generation AND telegram_chat_id IS NOT NULL",
                new { Generation = generationId }, cancellationToken: cancellationToken))).AsList();
        }

        /// <summary>
        /// Every user the inventory saw, across all user-tier key shapes.
        /// </summary>
        internal static async Task<IReadOnlyCollection<long>> UserIdsAsync(PostgresDatabase database,
            long generationId, CancellationToken cancellationToken = default)
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            return (await connection.QueryAsync<long>(new CommandDefinition(@"
SELECT DISTINCT telegram_user_id FROM migration_key_inventory
 WHERE generation_id = @Generation AND telegram_user_id IS NOT NULL",
                new { Generation = generationId }, cancellationToken: cancellationToken))).AsList();
        }

        /// <summary>
        /// Report message ids by group, parsed from the <c>flagged:{groupId}:{messageId}</c> keys.
        /// <para>
        /// <c>flaggedReply:</c> keys share the <c>report</c> class but are a companion index rather
        /// than a report, so they are excluded here.
        /// </para>
        /// </summary>
        internal static async Task<IReadOnlyDictionary<long, IReadOnlyCollection<long>>>
            ReportMessageIdsAsync(PostgresDatabase database, long generationId,
                CancellationToken cancellationToken = default)
        {
            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            var rows = await connection.QueryAsync<(long GroupId, long MessageId)>(new CommandDefinition(@"
SELECT telegram_chat_id AS GroupId, split_part(redis_key, ':', 3)::bigint AS MessageId
  FROM migration_key_inventory
 WHERE generation_id = @Generation AND key_class = @Class
   AND telegram_chat_id IS NOT NULL
   AND redis_key LIKE 'flagged:%'
   AND split_part(redis_key, ':', 3) ~ '^[0-9]+$'",
                new { Generation = generationId, Class = MigrationKeyClasses.Report },
                cancellationToken: cancellationToken));

            var result = new Dictionary<long, HashSet<long>>();
            foreach (var (groupId, messageId) in rows)
            {
                if (!result.TryGetValue(groupId, out var ids))
                    result[groupId] = ids = new HashSet<long>();
                ids.Add(messageId);
            }

            var projected = new Dictionary<long, IReadOnlyCollection<long>>(result.Count);
            foreach (var pair in result) projected[pair.Key] = pair.Value;
            return projected;
        }

        /// <summary>
        /// The generation every domain should read, or null when no pass has ever completed.
        /// A partial generation is never offered: it would silently migrate a subset.
        /// </summary>
        internal static Task<(long GenerationId, DateTimeOffset CompletedAt)?> LatestAsync(
            PostgresDatabase database, CancellationToken cancellationToken = default) =>
            KeyspaceInventoryScanner.LatestCompletedAsync(database, cancellationToken);
    }
}
