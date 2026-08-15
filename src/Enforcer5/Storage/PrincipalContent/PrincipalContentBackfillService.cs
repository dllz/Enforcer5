using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Enforcer5.Storage.PrincipalContent
{
    internal sealed class PrincipalContentBackfillService
    {
        private readonly LegacyPrincipalContentReader _reader = new LegacyPrincipalContentReader();
        private readonly PostgresDatabase _database;
        private readonly UserRepository _users;
        private readonly GroupRepository _groups;
        private readonly GroupEntitlementRepository _entitlements;
        private readonly GroupContentRepository _content;
        private readonly CustomCommandRepository _commands;
        private readonly CustomCommandConflictRepository _commandConflicts;
        internal int ReconciliationMismatches { get; private set; }

        internal PrincipalContentBackfillService(PostgresDatabase database)
        {
            _database = database;
            _users = new UserRepository(database);
            _groups = new GroupRepository(database);
            _entitlements = new GroupEntitlementRepository(database);
            _content = new GroupContentRepository(database);
            _commands = new CustomCommandRepository(database);
            _commandConflicts = new CustomCommandConflictRepository(database);
        }

        internal async Task<PrincipalContentBackfillResult> RunRedisPrimaryAsync(
            IEnumerable<long> userIds, bool reconcile = false,
            CancellationToken cancellationToken = default)
        {
            // Discovery unions bot:groupsid with a scan, because the set alone omits 7,951 groups
            // that have data. See LegacyPrincipalContentReader.DiscoverGroupIdsAsync.
            var groupIds = await LegacyPrincipalContentReader.DiscoverGroupIdsAsync(cancellationToken);
            return await RunRedisPrimaryAsync(userIds, groupIds, reconcile, cancellationToken);
        }

        internal async Task<PrincipalContentBackfillResult> RunRedisPrimaryAsync(
            IEnumerable<long> userIds, IEnumerable<long> groupIds, bool reconcile = false,
            CancellationToken cancellationToken = default)
        {
            if (StorageModeProvider.Current != StorageMode.RedisPrimaryPostgresShadow)
                throw new System.InvalidOperationException("Redis backfill requires RedisPrimaryPostgresShadow mode.");

            var snapshot = await _reader.ReadAsync(userIds, groupIds, cancellationToken);
            var result = new PrincipalContentBackfillResult();
            if (reconcile)
                ReconciliationMismatches = await ReconcileAsync(_database, snapshot, cancellationToken);
            await _users.UpsertManyAsync(snapshot.Users, cancellationToken);
            result.UsersWritten = snapshot.Users.Count;
            foreach (var group in snapshot.Groups)
            {
                await _groups.UpsertAsync(group, cancellationToken);
                result.GroupsWritten++;
            }
            foreach (var entitlement in snapshot.Entitlements)
            {
                await _entitlements.UpsertAsync(entitlement, cancellationToken);
                result.EntitlementsWritten++;
            }
            foreach (var item in snapshot.ContentItems)
            {
                await _content.UpsertAsync(item, cancellationToken);
                result.ContentItemsWritten++;
            }
            foreach (var command in snapshot.CustomCommands)
            {
                await _commands.UpsertAsync(command, cancellationToken);
                result.CustomCommandsWritten++;
            }
            foreach (var conflict in snapshot.CustomCommandConflicts.GroupBy(value =>
                         (value.ChatId, value.Trigger)))
                await _commandConflicts.ReplaceAsync(conflict.Key.ChatId, conflict.Key.Trigger,
                    conflict.ToArray(), cancellationToken);

            result.DataQualityIssues = await RecordDataQualityIssuesAsync(snapshot, cancellationToken);
            return result;
        }

        /// <summary>
        /// Records defects that do not stop the record migrating - a legacy user with no name, for
        /// example. Severity is 'warning' rather than 'blocking': the data is migrated, and the gap
        /// is left visible for follow-up instead of being used as grounds for deletion.
        /// </summary>
        private async Task<int> RecordDataQualityIssuesAsync(PrincipalContentBackfill snapshot,
            CancellationToken cancellationToken)
        {
            if (snapshot.DataQualityIssues.Count == 0) return 0;

            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var runId = await connection.ExecuteScalarAsync<long>(new Dapper.CommandDefinition(@"
INSERT INTO migration_runs (mode, status, completed_at, details)
VALUES ('principal-content-data-quality', 'completed', now(), '{}'::jsonb)
RETURNING id", transaction: transaction, cancellationToken: cancellationToken));

            var written = await connection.ExecuteAsync(new Dapper.CommandDefinition(@"
INSERT INTO migration_exceptions
    (migration_run_id, domain, source_key, source_field, severity, reason)
VALUES (@RunId, 'principal-content', @SourceKey, @SourceField, 'warning', @Reason)",
                snapshot.DataQualityIssues.Select(issue => new
                {
                    RunId = runId,
                    issue.SourceKey,
                    issue.SourceField,
                    issue.Reason
                }).ToArray(), transaction, cancellationToken: cancellationToken));

            await transaction.CommitAsync(cancellationToken);
            return written;
        }

        internal static async Task<int> ReconcileAsync(PostgresDatabase database,
            PrincipalContentBackfill snapshot, CancellationToken cancellationToken = default)
        {
            var groupIds = snapshot.Groups.Select(value => value.ChatId).Distinct().ToArray();
            var userIds = snapshot.Users.Select(value => value.UserId).Distinct().ToArray();
            var entitlementKeys = snapshot.Entitlements
                .Select(value => (value.ChatId, value.Entitlement)).ToHashSet();
            var contentKeys = snapshot.ContentItems
                .Select(value => (value.ChatId, value.ContentType)).ToHashSet();
            var commandKeys = snapshot.CustomCommands
                .Select(value => (value.ChatId, value.Trigger)).ToHashSet();

            await using var connection = await database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var mismatches = 0;

            if (userIds.Length > 0)
                mismatches += await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT count(*) FROM unnest(@Ids, @Names, @Usernames) AS r(telegram_user_id, name, username)
LEFT JOIN telegram_accounts a ON a.telegram_user_id = r.telegram_user_id
LEFT JOIN user_identity_links l ON l.account_id = a.account_id AND l.unlinked_at IS NULL
LEFT JOIN telegram_account_profiles profile ON profile.account_id = a.account_id AND profile.valid_to IS NULL
WHERE a.account_id IS NULL OR l.identity_link_id IS NULL OR profile.display_name IS DISTINCT FROM r.name
OR profile.username IS DISTINCT FROM r.username",
                    new { Ids = userIds, Names = snapshot.Users.Select(x => x.Name).ToArray(),
                        Usernames = snapshot.Users.Select(x => x.Username).ToArray() }, transaction,
                    cancellationToken: cancellationToken));

            if (groupIds.Length > 0)
            {
                mismatches += await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT count(*) FROM unnest(@Ids, @Names) AS r(telegram_chat_id, name)
LEFT JOIN telegram_chats c ON c.telegram_chat_id = r.telegram_chat_id
LEFT JOIN groups g ON g.chat_id = c.chat_id
LEFT JOIN telegram_chat_profiles profile ON profile.chat_id = c.chat_id AND profile.valid_to IS NULL
WHERE g.group_id IS NULL OR COALESCE(profile.title, g.title) IS DISTINCT FROM r.name", new { Ids = groupIds,
                    Names = snapshot.Groups.Select(x => x.Name).ToArray() }, transaction,
                    cancellationToken: cancellationToken));

                var entitlements = await connection.QueryAsync<(long ChatId, string Entitlement)>(
                    new CommandDefinition(@"SELECT c.telegram_chat_id AS ChatId, e.entitlement AS Entitlement
FROM group_entitlements e JOIN groups g ON g.group_id = e.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id WHERE c.telegram_chat_id = ANY(@GroupIds) AND e.enabled", new { GroupIds = groupIds },
                        transaction, cancellationToken: cancellationToken));
                foreach (var value in snapshot.Entitlements)
                    if (!entitlements.Contains((value.ChatId, value.Entitlement))) mismatches++;
                foreach (var value in entitlements.Where(value => !entitlementKeys.Contains(value)))
                {
                    mismatches++;
                    await connection.ExecuteAsync(new CommandDefinition(@"UPDATE group_entitlements e SET enabled = FALSE
FROM groups g JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE e.group_id = g.group_id AND c.telegram_chat_id = @ChatId AND e.entitlement = @Entitlement",
                        new { value.ChatId, value.Entitlement }, transaction,
                        cancellationToken: cancellationToken));
                }

                var content = await connection.QueryAsync<(long ChatId, string ContentType)>(
                    new CommandDefinition(@"SELECT c.telegram_chat_id AS ChatId, i.content_type AS ContentType
FROM group_content_items i JOIN groups g ON g.group_id = i.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id WHERE c.telegram_chat_id = ANY(@GroupIds)", new { GroupIds = groupIds }, transaction,
                        cancellationToken: cancellationToken));
                foreach (var value in snapshot.ContentItems)
                {
                    var current = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(@"
SELECT i.content FROM group_content_items i JOIN groups g ON g.group_id = i.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE c.telegram_chat_id = @ChatId AND i.content_type = @ContentType",
                        value, transaction, cancellationToken: cancellationToken));
                    if (!string.Equals(current, value.Content, System.StringComparison.Ordinal)) mismatches++;
                }
                foreach (var value in content.Where(value => !contentKeys.Contains(value)))
                {
                    mismatches++;
                    await connection.ExecuteAsync(new CommandDefinition(@"DELETE FROM group_content_items i USING groups g, telegram_chats c
WHERE i.group_id = g.group_id AND g.chat_id = c.chat_id
AND c.telegram_chat_id = @ChatId AND i.content_type = @ContentType",
                        new { value.ChatId, value.ContentType }, transaction,
                        cancellationToken: cancellationToken));
                }

                var commands = await connection.QueryAsync<(long ChatId, string Trigger)>(
                    new CommandDefinition(@"SELECT c.telegram_chat_id AS ChatId, x.trigger AS Trigger
FROM custom_commands x JOIN groups g ON g.group_id = x.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id WHERE c.telegram_chat_id = ANY(@GroupIds)", new { GroupIds = groupIds }, transaction,
                        cancellationToken: cancellationToken));
                foreach (var value in snapshot.CustomCommands)
                {
                    var normalized = value with
                    {
                        MediaType = value.MediaType == "gif" ? "animation" : value.MediaType
                    };
                    var equal = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT EXISTS (SELECT 1 FROM custom_commands x JOIN groups g ON g.group_id = x.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id WHERE c.telegram_chat_id = @ChatId AND x.trigger = @Trigger
AND x.content IS NOT DISTINCT FROM @Content AND x.media_type IS NOT DISTINCT FROM @MediaType
AND x.file_id IS NOT DISTINCT FROM @FileId
AND x.file_owner_edition IS NOT DISTINCT FROM @FileOwnerEdition
AND x.caption IS NOT DISTINCT FROM @Caption)", normalized,
                        transaction, cancellationToken: cancellationToken));
                    if (!equal) mismatches++;
                }
                foreach (var value in commands.Where(value => !commandKeys.Contains(value)))
                {
                    var conflictBacked = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT EXISTS (SELECT 1 FROM custom_command_conflict_candidates x JOIN groups g ON g.group_id = x.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE c.telegram_chat_id = @ChatId AND x.trigger = @Trigger AND x.resolution = 'selected')",
                        new { value.ChatId, value.Trigger }, transaction,
                        cancellationToken: cancellationToken));
                    if (conflictBacked) continue;
                    mismatches++;
                    await connection.ExecuteAsync(new CommandDefinition(@"DELETE FROM custom_commands x USING groups g, telegram_chats c
WHERE x.group_id = g.group_id AND g.chat_id = c.chat_id
AND c.telegram_chat_id = @ChatId AND x.trigger = @Trigger",
                        new { value.ChatId, value.Trigger }, transaction,
                        cancellationToken: cancellationToken));
                }
            }

            await transaction.CommitAsync(cancellationToken);
            System.Console.Error.WriteLine($"Principal/content reconciliation mismatches={mismatches}.");
            return mismatches;
        }
    }
}
