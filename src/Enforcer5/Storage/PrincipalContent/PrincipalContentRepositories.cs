using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;
using Npgsql;

namespace Enforcer5.Storage.PrincipalContent
{
    internal abstract class PrincipalContentRepository
    {
        protected PrincipalContentRepository(PostgresDatabase database)
        {
            Database = database ?? throw new ArgumentNullException(nameof(database));
        }

        protected PostgresDatabase Database { get; }

        protected static async Task EnqueueMirrorAsync(NpgsqlConnection connection,
            NpgsqlTransaction transaction, string domain, string operation, object payload,
            CancellationToken cancellationToken)
        {
            if (!StorageModeProvider.RedisMirrorEnabled) return;

            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO compatibility_outbox (id, domain, ordering_key, operation, payload, created_at, next_attempt_at)
VALUES (@Id, @Domain, @OrderingKey, @Operation, CAST(@Payload AS jsonb), clock_timestamp(), clock_timestamp())",
                new
                {
                    Id = Guid.NewGuid(),
                    Domain = domain,
                    OrderingKey = MirrorOrderingKey(domain, payload),
                    Operation = operation,
                    Payload = JsonConvert.SerializeObject(payload, new JsonSerializerSettings
                    {
                        ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
                    })
                }, transaction, cancellationToken: cancellationToken));
        }

        private static string MirrorOrderingKey(string domain, object payload)
        {
            var json = Newtonsoft.Json.Linq.JObject.FromObject(payload);
            return domain switch
            {
                "users" => $"users:{json.Value<long>("userId")}",
                "groups" => $"groups:{json.Value<long>("chatId")}",
                "group-entitlements" => $"entitlement:{json.Value<long>("chatId")}:{json.Value<string>("entitlement")}",
                "group-content" => $"content:{json.Value<long>("chatId")}:{json.Value<string>("contentType")}",
                "custom-commands" => $"command:{json.Value<long>("chatId")}:{json.Value<string>("trigger")}",
                _ => throw new InvalidOperationException($"Unsupported mirror domain '{domain}'.")
            };
        }
    }

    internal sealed class UserRepository : PrincipalContentRepository
    {
        internal UserRepository(PostgresDatabase database) : base(database) { }

        internal async Task<UserPrincipal> GetAsync(long userId, CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            return await connection.QuerySingleOrDefaultAsync<UserPrincipal>(new CommandDefinition(@"
SELECT user_id AS UserId, name AS Name, username AS Username
FROM users WHERE user_id = @UserId", new { UserId = userId }, cancellationToken: cancellationToken));
        }

        internal async Task<UserPrincipal> GetByUsernameAsync(string username,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            return await connection.QuerySingleOrDefaultAsync<UserPrincipal>(new CommandDefinition(@"
SELECT user_id AS UserId, name AS Name, username AS Username
FROM users WHERE lower(username) = lower(@Username)
ORDER BY updated_at DESC LIMIT 1", new { Username = username }, cancellationToken: cancellationToken));
        }

        internal async Task UpsertAsync(UserPrincipal user, CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var previousUsername = await connection.ExecuteScalarAsync<string>(new CommandDefinition(
                "SELECT username FROM users WHERE user_id = @UserId FOR UPDATE",
                new { user.UserId }, transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO users (user_id, name, username) VALUES (@UserId, @Name, @Username)
ON CONFLICT (user_id) DO UPDATE
SET name = EXCLUDED.name, username = EXCLUDED.username, updated_at = now()",
                user, transaction, cancellationToken: cancellationToken));
            await EnqueueMirrorAsync(connection, transaction, "users", "upsert", new
            {
                userId = user.UserId, name = user.Name, username = user.Username, previousUsername
            }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    internal sealed class GroupRepository : PrincipalContentRepository
    {
        internal GroupRepository(PostgresDatabase database) : base(database) { }

        internal async Task<GroupPrincipal> GetAsync(long chatId, CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            return await connection.QuerySingleOrDefaultAsync<GroupPrincipal>(new CommandDefinition(@"
SELECT chat_id AS ChatId, name AS Name FROM groups WHERE chat_id = @ChatId",
                new { ChatId = chatId }, cancellationToken: cancellationToken));
        }

        internal async Task UpsertAsync(GroupPrincipal group, CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO groups (chat_id, name) VALUES (@ChatId, @Name)
ON CONFLICT (chat_id) DO UPDATE SET name = EXCLUDED.name, updated_at = now()",
                group, transaction, cancellationToken: cancellationToken));
            await EnqueueMirrorAsync(connection, transaction, "groups", "upsert", group, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    internal sealed class GroupEntitlementRepository : PrincipalContentRepository
    {
        internal GroupEntitlementRepository(PostgresDatabase database) : base(database) { }

        internal async Task<IReadOnlyList<GroupEntitlement>> GetAsync(long chatId,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            var rows = await connection.QueryAsync<GroupEntitlement>(new CommandDefinition(@"
SELECT chat_id AS ChatId, entitlement AS Entitlement, enabled AS Enabled
FROM group_entitlements WHERE chat_id = @ChatId ORDER BY entitlement",
                new { ChatId = chatId }, cancellationToken: cancellationToken));
            return rows.AsList();
        }

        internal async Task UpsertAsync(GroupEntitlement entitlement,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO group_entitlements (chat_id, entitlement, enabled)
VALUES (@ChatId, @Entitlement, @Enabled)
ON CONFLICT (chat_id, entitlement) DO UPDATE
SET enabled = EXCLUDED.enabled, updated_at = now()", entitlement, transaction,
                cancellationToken: cancellationToken));
            await EnqueueMirrorAsync(connection, transaction, "group-entitlements", "upsert",
                entitlement, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    internal sealed class GroupContentRepository : PrincipalContentRepository
    {
        internal GroupContentRepository(PostgresDatabase database) : base(database) { }

        internal async Task<GroupContentItem> GetAsync(long chatId, string contentType,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            return await connection.QuerySingleOrDefaultAsync<GroupContentItem>(new CommandDefinition(@"
SELECT chat_id AS ChatId, content_type AS ContentType, content AS Content
FROM group_content_items WHERE chat_id = @ChatId AND content_type = @ContentType",
                new { ChatId = chatId, ContentType = contentType }, cancellationToken: cancellationToken));
        }

        internal async Task UpsertAsync(GroupContentItem item, CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO group_content_items (chat_id, content_type, content)
VALUES (@ChatId, @ContentType, @Content)
ON CONFLICT (chat_id, content_type) DO UPDATE
SET content = EXCLUDED.content, updated_at = now()", item, transaction,
                cancellationToken: cancellationToken));
            await EnqueueMirrorAsync(connection, transaction, "group-content", "upsert", item, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        internal async Task DeleteAsync(long chatId, string contentType,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
DELETE FROM group_content_items WHERE chat_id = @ChatId AND content_type = @ContentType",
                new { ChatId = chatId, ContentType = contentType }, transaction,
                cancellationToken: cancellationToken));
            await EnqueueMirrorAsync(connection, transaction, "group-content", "delete",
                new { chatId, contentType }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    internal sealed class CustomCommandRepository : PrincipalContentRepository
    {
        internal CustomCommandRepository(PostgresDatabase database) : base(database) { }

        internal async Task<CustomCommand> GetAsync(long chatId, string trigger,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            return await connection.QuerySingleOrDefaultAsync<CustomCommand>(new CommandDefinition(@"
SELECT chat_id AS ChatId, trigger AS Trigger, content AS Content, media_type AS MediaType,
       file_id AS FileId, caption AS Caption
FROM custom_commands WHERE chat_id = @ChatId AND trigger = @Trigger",
                new { ChatId = chatId, Trigger = trigger }, cancellationToken: cancellationToken));
        }

        internal async Task<IReadOnlyList<CustomCommand>> ListAsync(long chatId,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            var rows = await connection.QueryAsync<CustomCommand>(new CommandDefinition(@"
SELECT chat_id AS ChatId, trigger AS Trigger, content AS Content, media_type AS MediaType,
       file_id AS FileId, caption AS Caption
FROM custom_commands WHERE chat_id = @ChatId ORDER BY trigger",
                new { ChatId = chatId }, cancellationToken: cancellationToken));
            return rows.AsList();
        }

        internal async Task UpsertAsync(CustomCommand command, CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO custom_commands (chat_id, trigger, content, media_type, file_id, caption)
VALUES (@ChatId, @Trigger, @Content, @MediaType, @FileId, @Caption)
ON CONFLICT (chat_id, trigger) DO UPDATE
SET content = EXCLUDED.content, media_type = EXCLUDED.media_type,
    file_id = EXCLUDED.file_id, caption = EXCLUDED.caption, updated_at = now()",
                command, transaction, cancellationToken: cancellationToken));
            await EnqueueMirrorAsync(connection, transaction, "custom-commands", "upsert",
                command, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        internal async Task DeleteAsync(long chatId, string trigger,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
DELETE FROM custom_commands WHERE chat_id = @ChatId AND trigger = @Trigger",
                new { ChatId = chatId, Trigger = trigger }, transaction,
                cancellationToken: cancellationToken));
            await EnqueueMirrorAsync(connection, transaction, "custom-commands", "delete",
                new { chatId, trigger }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }
}
