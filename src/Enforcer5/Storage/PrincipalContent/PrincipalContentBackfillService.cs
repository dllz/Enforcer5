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
        internal int ReconciliationMismatches { get; private set; }

        internal PrincipalContentBackfillService(PostgresDatabase database)
        {
            _database = database;
            _users = new UserRepository(database);
            _groups = new GroupRepository(database);
            _entitlements = new GroupEntitlementRepository(database);
            _content = new GroupContentRepository(database);
            _commands = new CustomCommandRepository(database);
        }

        internal async Task<PrincipalContentBackfillResult> RunRedisPrimaryAsync(
            IEnumerable<long> userIds, bool reconcile = false,
            CancellationToken cancellationToken = default)
        {
            if (StorageModeProvider.Current != StorageMode.RedisPrimaryPostgresShadow)
                throw new System.InvalidOperationException("Redis backfill requires RedisPrimaryPostgresShadow mode.");

            var snapshot = await _reader.ReadAsync(userIds, cancellationToken);
            var result = new PrincipalContentBackfillResult();
            if (reconcile)
                ReconciliationMismatches = await ReconcileAsync(_database, snapshot, cancellationToken);
            foreach (var user in snapshot.Users)
            {
                await _users.UpsertAsync(user, cancellationToken);
                result.UsersWritten++;
            }
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
            return result;
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
SELECT count(*) FROM users p
RIGHT JOIN unnest(@Ids, @Names, @Usernames) AS r(user_id, name, username) ON r.user_id = p.user_id
WHERE p.user_id IS NULL OR p.name IS DISTINCT FROM r.name OR p.username IS DISTINCT FROM r.username",
                    new { Ids = userIds, Names = snapshot.Users.Select(x => x.Name).ToArray(),
                        Usernames = snapshot.Users.Select(x => x.Username).ToArray() }, transaction,
                    cancellationToken: cancellationToken));

            if (groupIds.Length > 0)
            {
                mismatches += await connection.ExecuteScalarAsync<int>(new CommandDefinition(@"
SELECT count(*) FROM groups p
RIGHT JOIN unnest(@Ids, @Names) AS r(chat_id, name) ON r.chat_id = p.chat_id
WHERE p.chat_id IS NULL OR p.name IS DISTINCT FROM r.name", new { Ids = groupIds,
                    Names = snapshot.Groups.Select(x => x.Name).ToArray() }, transaction,
                    cancellationToken: cancellationToken));

                var entitlements = await connection.QueryAsync<(long ChatId, string Entitlement)>(
                    new CommandDefinition(@"SELECT chat_id AS ChatId, entitlement AS Entitlement
FROM group_entitlements WHERE chat_id = ANY(@GroupIds) AND enabled", new { GroupIds = groupIds },
                        transaction, cancellationToken: cancellationToken));
                foreach (var value in snapshot.Entitlements)
                    if (!entitlements.Contains((value.ChatId, value.Entitlement))) mismatches++;
                foreach (var value in entitlements.Where(value => !entitlementKeys.Contains(value)))
                {
                    mismatches++;
                    await connection.ExecuteAsync(new CommandDefinition(@"UPDATE group_entitlements
SET enabled = FALSE, updated_at = now() WHERE chat_id = @ChatId AND entitlement = @Entitlement",
                        value, transaction, cancellationToken: cancellationToken));
                }

                var content = await connection.QueryAsync<(long ChatId, string ContentType)>(
                    new CommandDefinition(@"SELECT chat_id AS ChatId, content_type AS ContentType
FROM group_content_items WHERE chat_id = ANY(@GroupIds)", new { GroupIds = groupIds }, transaction,
                        cancellationToken: cancellationToken));
                foreach (var value in snapshot.ContentItems)
                {
                    var current = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(@"
SELECT content FROM group_content_items WHERE chat_id = @ChatId AND content_type = @ContentType",
                        value, transaction, cancellationToken: cancellationToken));
                    if (!string.Equals(current, value.Content, System.StringComparison.Ordinal)) mismatches++;
                }
                foreach (var value in content.Where(value => !contentKeys.Contains(value)))
                {
                    mismatches++;
                    await connection.ExecuteAsync(new CommandDefinition(@"DELETE FROM group_content_items
WHERE chat_id = @ChatId AND content_type = @ContentType", value, transaction,
                        cancellationToken: cancellationToken));
                }

                var commands = await connection.QueryAsync<(long ChatId, string Trigger)>(
                    new CommandDefinition(@"SELECT chat_id AS ChatId, trigger AS Trigger
FROM custom_commands WHERE chat_id = ANY(@GroupIds)", new { GroupIds = groupIds }, transaction,
                        cancellationToken: cancellationToken));
                foreach (var value in snapshot.CustomCommands)
                {
                    var equal = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT EXISTS (SELECT 1 FROM custom_commands WHERE chat_id = @ChatId AND trigger = @Trigger
AND content IS NOT DISTINCT FROM @Content AND media_type IS NOT DISTINCT FROM @MediaType
AND file_id IS NOT DISTINCT FROM @FileId AND caption IS NOT DISTINCT FROM @Caption)", value,
                        transaction, cancellationToken: cancellationToken));
                    if (!equal) mismatches++;
                }
                foreach (var value in commands.Where(value => !commandKeys.Contains(value)))
                {
                    mismatches++;
                    await connection.ExecuteAsync(new CommandDefinition(@"DELETE FROM custom_commands
WHERE chat_id = @ChatId AND trigger = @Trigger", value, transaction,
                        cancellationToken: cancellationToken));
                }
            }

            await transaction.CommitAsync(cancellationToken);
            System.Console.Error.WriteLine($"Principal/content reconciliation mismatches={mismatches}.");
            return mismatches;
        }
    }
}
