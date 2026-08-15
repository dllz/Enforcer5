using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;
using Npgsql;

namespace Enforcer5.Storage.PrincipalContent
{
    internal abstract class PrincipalContentRepository
    {
        protected PrincipalContentRepository(PostgresDatabase database) =>
            Database = database ?? throw new ArgumentNullException(nameof(database));

        protected PostgresDatabase Database { get; }

        protected static async Task EnqueueMirrorAsync(NpgsqlConnection connection,
            NpgsqlTransaction transaction, string domain, string operation, object payload,
            CancellationToken cancellationToken)
        {
            // Shared across user-tier and group-tier domains, and the group is only inside the
            // payload. User-tier dominates here, so this follows the process-wide mirror flag.
            if (!StorageRouting.RedisMirrorEnabled) return;
            var json = Newtonsoft.Json.Linq.JObject.FromObject(payload);
            var orderingKey = domain switch
            {
                "users" => $"users:{json.Value<long>("userId")}",
                "groups" => $"groups:{json.Value<long>("chatId")}",
                "group-entitlements" => $"entitlement:{json.Value<long>("chatId")}:{json.Value<string>("entitlement")}",
                "group-content" => $"content:{json.Value<long>("chatId")}:{json.Value<string>("contentType")}",
                "custom-commands" => $"command:{json.Value<long>("chatId")}:{json.Value<string>("trigger")}",
                _ => throw new InvalidOperationException($"Unsupported mirror domain '{domain}'.")
            };
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO compatibility_outbox (id, domain, ordering_key, operation, payload, created_at, next_attempt_at)
VALUES (@Id, @Domain, @OrderingKey, @Operation, CAST(@Payload AS jsonb), clock_timestamp(), clock_timestamp())",
                new { Id = Guid.NewGuid(), Domain = domain, OrderingKey = orderingKey, Operation = operation,
                    Payload = JsonConvert.SerializeObject(payload, new JsonSerializerSettings
                    { ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver() }) },
                transaction, cancellationToken: cancellationToken));
        }

        protected async Task<long> EnsureGroupIdAsync(NpgsqlConnection connection,
            NpgsqlTransaction transaction, long telegramChatId, CancellationToken cancellationToken) =>
            (await PrincipalResolver.EnsureGroupAsync(connection, transaction, telegramChatId,
                cancellationToken: cancellationToken)).GroupId;
    }

    internal sealed class UserRepository : PrincipalContentRepository
    {
        internal UserRepository(PostgresDatabase database) : base(database) { }

        internal async Task<UserPrincipal> GetAsync(long userId, CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            return await connection.QuerySingleOrDefaultAsync<UserPrincipal>(new CommandDefinition(@"
SELECT a.telegram_user_id AS UserId, p.display_name AS Name, p.username AS Username, a.is_bot AS IsBot
FROM telegram_accounts a
JOIN user_identity_links l ON l.account_id = a.account_id AND l.unlinked_at IS NULL
LEFT JOIN telegram_account_profiles p ON p.account_id = a.account_id AND p.valid_to IS NULL
WHERE a.telegram_user_id = @UserId", new { UserId = userId }, cancellationToken: cancellationToken));
        }

        internal async Task<UserPrincipal> GetByUsernameAsync(string username,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            return await connection.QuerySingleOrDefaultAsync<UserPrincipal>(new CommandDefinition(@"
SELECT a.telegram_user_id AS UserId, p.display_name AS Name, p.username AS Username, a.is_bot AS IsBot
FROM telegram_account_profiles p JOIN telegram_accounts a ON a.account_id = p.account_id
JOIN user_identity_links l ON l.account_id = a.account_id AND l.unlinked_at IS NULL
WHERE p.valid_to IS NULL AND p.username = @Username ORDER BY p.valid_from DESC LIMIT 1",
                new { Username = username }, cancellationToken: cancellationToken));
        }

        internal async Task UpsertAsync(UserPrincipal user, CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var previousUsername = await connection.ExecuteScalarAsync<string>(new CommandDefinition(@"
SELECT p.username FROM telegram_accounts a LEFT JOIN telegram_account_profiles p
ON p.account_id = a.account_id AND p.valid_to IS NULL WHERE a.telegram_user_id = @UserId",
                new { user.UserId }, transaction, cancellationToken: cancellationToken));
            await PrincipalResolver.EnsureUserAsync(connection, transaction, user.UserId, user.Name,
                user.Username, user.IsBot, cancellationToken);
            await EnqueueMirrorAsync(connection, transaction, "users", "upsert", new
            { userId = user.UserId, name = user.Name, username = user.Username, previousUsername }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        internal async Task UpsertManyAsync(IReadOnlyCollection<UserPrincipal> users,
            CancellationToken cancellationToken = default)
        {
            if (users.Count == 0) return;
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            foreach (var batch in users.Chunk(2000))
            {
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await connection.ExecuteAsync(new CommandDefinition(@"
CREATE TEMP TABLE IF NOT EXISTS principal_stage (
    telegram_user_id bigint PRIMARY KEY, display_name text, username text) ON COMMIT DELETE ROWS;
INSERT INTO principal_stage (telegram_user_id, display_name, username)
SELECT * FROM unnest(@Ids, @Names, @Usernames)
ON CONFLICT (telegram_user_id) DO UPDATE SET display_name = EXCLUDED.display_name,
    username = EXCLUDED.username;

INSERT INTO telegram_accounts (telegram_user_id, is_bot, first_seen_at, last_seen_at)
SELECT telegram_user_id, FALSE, now(), now() FROM principal_stage
ON CONFLICT (telegram_user_id) DO UPDATE SET last_seen_at = EXCLUDED.last_seen_at;

WITH missing AS MATERIALIZED (
    SELECT a.account_id FROM telegram_accounts a
    JOIN principal_stage s ON s.telegram_user_id = a.telegram_user_id
    LEFT JOIN user_identity_links l ON l.account_id = a.account_id AND l.unlinked_at IS NULL
    WHERE l.identity_link_id IS NULL
), mapped AS MATERIALIZED (
    SELECT account_id, nextval(pg_get_serial_sequence('users', 'user_id')) AS user_id FROM missing
), inserted AS (
    INSERT INTO users (user_id, created_at, updated_at) OVERRIDING SYSTEM VALUE
    SELECT user_id, now(), now() FROM mapped RETURNING user_id
)
INSERT INTO user_identity_links (user_id, account_id, source, reason, linked_at)
SELECT m.user_id, m.account_id, 'migration', 'legacy Redis principal', now()
FROM mapped m JOIN inserted i ON i.user_id = m.user_id;

UPDATE telegram_account_profiles p SET valid_to = now()
FROM telegram_accounts a JOIN principal_stage s ON s.telegram_user_id = a.telegram_user_id
WHERE p.account_id = a.account_id AND p.valid_to IS NULL
  AND (p.display_name IS DISTINCT FROM s.display_name OR p.username IS DISTINCT FROM s.username);

INSERT INTO telegram_account_profiles (account_id, display_name, username, valid_from)
SELECT a.account_id, s.display_name, s.username, now()
FROM principal_stage s JOIN telegram_accounts a ON a.telegram_user_id = s.telegram_user_id
LEFT JOIN telegram_account_profiles p ON p.account_id = a.account_id AND p.valid_to IS NULL
WHERE p.profile_id IS NULL", new
                {
                    Ids = batch.Select(x => x.UserId).ToArray(),
                    Names = batch.Select(x => x.Name).ToArray(),
                    Usernames = batch.Select(x => x.Username).ToArray()
                }, transaction, cancellationToken: cancellationToken));
                await transaction.CommitAsync(cancellationToken);
            }
        }
    }

    internal sealed class GroupRepository : PrincipalContentRepository
    {
        internal GroupRepository(PostgresDatabase database) : base(database) { }

        internal async Task<GroupPrincipal> GetAsync(long chatId, CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            return await connection.QuerySingleOrDefaultAsync<GroupPrincipal>(new CommandDefinition(@"
SELECT c.telegram_chat_id AS ChatId, COALESCE(p.title, g.title) AS Name,
COALESCE(p.username, g.username) AS Username, c.chat_type AS ChatType
FROM groups g JOIN telegram_chats c ON c.chat_id = g.chat_id
LEFT JOIN telegram_chat_profiles p ON p.chat_id = c.chat_id AND p.valid_to IS NULL
WHERE c.telegram_chat_id = @ChatId", new { ChatId = chatId }, cancellationToken: cancellationToken));
        }

        internal async Task UpsertAsync(GroupPrincipal group, CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await PrincipalResolver.EnsureGroupAsync(connection, transaction, group.ChatId, group.Name,
                group.Username, group.ChatType, cancellationToken);
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
SELECT c.telegram_chat_id AS ChatId, e.entitlement AS Entitlement, e.enabled AS Enabled
FROM group_entitlements e JOIN groups g ON g.group_id = e.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE c.telegram_chat_id = @ChatId ORDER BY e.entitlement", new { ChatId = chatId },
                cancellationToken: cancellationToken));
            return rows.AsList();
        }

        internal async Task UpsertAsync(GroupEntitlement entitlement,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var groupId = await EnsureGroupIdAsync(connection, transaction, entitlement.ChatId, cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO group_entitlements (group_id, entitlement, enabled)
VALUES (@GroupId, @Entitlement, @Enabled)
ON CONFLICT (group_id, entitlement) DO UPDATE SET enabled = EXCLUDED.enabled",
                new { GroupId = groupId, entitlement.Entitlement, entitlement.Enabled }, transaction,
                cancellationToken: cancellationToken));
            await EnqueueMirrorAsync(connection, transaction, "group-entitlements", "upsert", entitlement, cancellationToken);
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
SELECT c.telegram_chat_id AS ChatId, i.content_type AS ContentType, i.content AS Content
FROM group_content_items i JOIN groups g ON g.group_id = i.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE c.telegram_chat_id = @ChatId AND i.content_type = @ContentType",
                new { ChatId = chatId, ContentType = contentType }, cancellationToken: cancellationToken));
        }

        internal async Task UpsertAsync(GroupContentItem item, CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var groupId = await EnsureGroupIdAsync(connection, transaction, item.ChatId, cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO group_content_items (group_id, content_type, content) VALUES (@GroupId, @ContentType, @Content)
ON CONFLICT (group_id, content_type) DO UPDATE SET content = EXCLUDED.content",
                new { GroupId = groupId, item.ContentType, item.Content }, transaction,
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
DELETE FROM group_content_items WHERE group_id = (SELECT g.group_id FROM groups g
JOIN telegram_chats c ON c.chat_id = g.chat_id WHERE c.telegram_chat_id = @ChatId)
AND content_type = @ContentType", new { ChatId = chatId, ContentType = contentType }, transaction,
                cancellationToken: cancellationToken));
            await EnqueueMirrorAsync(connection, transaction, "group-content", "delete", new { chatId, contentType }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    internal sealed class CustomCommandRepository : PrincipalContentRepository
    {
        internal CustomCommandRepository(PostgresDatabase database) : base(database) { }

        internal async Task<CustomCommand> GetAsync(long chatId, string trigger,
            CancellationToken cancellationToken = default)
        {
            trigger = LegacyPrincipalContentMapper.NormalizeCustomCommandTrigger(trigger);
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            return await connection.QuerySingleOrDefaultAsync<CustomCommand>(new CommandDefinition(@"
SELECT c.telegram_chat_id AS ChatId, x.trigger AS Trigger, x.content AS Content,
 x.media_type AS MediaType, x.file_id AS FileId, x.caption AS Caption,
 x.file_owner_edition AS FileOwnerEdition
FROM custom_commands x JOIN groups g ON g.group_id = x.group_id JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE c.telegram_chat_id = @ChatId AND x.trigger = @Trigger", new { ChatId = chatId, Trigger = trigger },
                cancellationToken: cancellationToken));
        }

        internal async Task<IReadOnlyList<CustomCommand>> ListAsync(long chatId,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            var rows = await connection.QueryAsync<CustomCommand>(new CommandDefinition(@"
SELECT c.telegram_chat_id AS ChatId, x.trigger AS Trigger, x.content AS Content,
 x.media_type AS MediaType, x.file_id AS FileId, x.caption AS Caption,
 x.file_owner_edition AS FileOwnerEdition
FROM custom_commands x JOIN groups g ON g.group_id = x.group_id JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE c.telegram_chat_id = @ChatId ORDER BY x.trigger", new { ChatId = chatId }, cancellationToken: cancellationToken));
            return rows.AsList();
        }

        internal async Task UpsertAsync(CustomCommand command, CancellationToken cancellationToken = default)
        {
            command = command with
            {
                Trigger = LegacyPrincipalContentMapper.NormalizeCustomCommandTrigger(command.Trigger)
            };
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var groupId = await EnsureGroupIdAsync(connection, transaction, command.ChatId, cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO custom_commands (group_id, trigger, content, media_type, file_id, file_owner_edition, caption)
VALUES (@GroupId, @Trigger, @Content, @MediaType, @FileId, @FileOwnerEdition, @Caption)
ON CONFLICT (group_id, trigger) DO UPDATE SET content = EXCLUDED.content,
media_type = EXCLUDED.media_type, file_id = EXCLUDED.file_id,
file_owner_edition = EXCLUDED.file_owner_edition, caption = EXCLUDED.caption",
                new { GroupId = groupId, command.Trigger, command.Content,
                    MediaType = LegacyPrincipalContentMapper.NormalizeCustomCommandMediaType(command.MediaType),
                    command.FileId,
                    FileOwnerEdition = command.FileId == null ? null :
                        command.FileOwnerEdition ?? Operations.OperationsStore.CurrentEdition,
                    command.Caption },
                transaction, cancellationToken: cancellationToken));
            await EnqueueMirrorAsync(connection, transaction, "custom-commands", "upsert", command, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        internal async Task DeleteAsync(long chatId, string trigger, CancellationToken cancellationToken = default)
        {
            trigger = LegacyPrincipalContentMapper.NormalizeCustomCommandTrigger(trigger);
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
DELETE FROM custom_commands WHERE group_id = (SELECT g.group_id FROM groups g JOIN telegram_chats c
ON c.chat_id = g.chat_id WHERE c.telegram_chat_id = @ChatId) AND trigger = @Trigger",
                new { ChatId = chatId, Trigger = trigger }, transaction, cancellationToken: cancellationToken));
            await EnqueueMirrorAsync(connection, transaction, "custom-commands", "delete", new { chatId, trigger }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
    }

    internal sealed class CustomCommandConflictRepository : PrincipalContentRepository
    {
        internal CustomCommandConflictRepository(PostgresDatabase database) : base(database) { }

        internal async Task ReplaceAsync(long chatId, string trigger,
            IReadOnlyCollection<CustomCommandConflictCandidate> candidates,
            CancellationToken cancellationToken = default)
        {
            trigger = LegacyPrincipalContentMapper.NormalizeCustomCommandTrigger(trigger);
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var groupId = await EnsureGroupIdAsync(connection, transaction, chatId, cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
DELETE FROM custom_commands WHERE group_id = @GroupId AND trigger = @Trigger
AND NOT EXISTS (SELECT 1 FROM custom_command_conflict_candidates
WHERE group_id = @GroupId AND trigger = @Trigger AND resolved_at IS NOT NULL)",
                new { GroupId = groupId, Trigger = trigger }, transaction,
                cancellationToken: cancellationToken));
            foreach (var candidate in candidates)
                await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO custom_command_conflict_candidates
(candidate_id, group_id, trigger, source_trigger, content, media_type, file_id, file_owner_edition, caption)
VALUES (@CandidateId, @GroupId, @Trigger, @SourceTrigger, @Content, @MediaType, @FileId, @FileOwnerEdition, @Caption)
ON CONFLICT (group_id, trigger, source_trigger) DO UPDATE SET
content = EXCLUDED.content, media_type = EXCLUDED.media_type, file_id = EXCLUDED.file_id,
file_owner_edition = EXCLUDED.file_owner_edition, caption = EXCLUDED.caption",
                    new { candidate.CandidateId, GroupId = groupId, Trigger = trigger, candidate.SourceTrigger,
                        candidate.Content,
                        MediaType = LegacyPrincipalContentMapper.NormalizeCustomCommandMediaType(candidate.MediaType),
                        candidate.FileId, candidate.FileOwnerEdition, candidate.Caption }, transaction,
                    cancellationToken: cancellationToken));
            await transaction.CommitAsync(cancellationToken);
        }

        internal async Task<IReadOnlyList<CustomCommandConflictCandidate>> GetAsync(long chatId,
            string trigger, CancellationToken cancellationToken = default)
        {
            trigger = LegacyPrincipalContentMapper.NormalizeCustomCommandTrigger(trigger);
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            var rows = await connection.QueryAsync<CustomCommandConflictCandidate>(new CommandDefinition(@"
SELECT x.candidate_id AS CandidateId, c.telegram_chat_id AS ChatId, x.trigger AS Trigger,
x.source_trigger AS SourceTrigger, x.content AS Content, x.media_type AS MediaType,
x.file_id AS FileId, x.caption AS Caption, x.file_owner_edition AS FileOwnerEdition
FROM custom_command_conflict_candidates x JOIN groups g ON g.group_id = x.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE c.telegram_chat_id = @ChatId AND x.trigger = @Trigger AND x.resolved_at IS NULL
ORDER BY x.source_trigger",
                new { ChatId = chatId, Trigger = trigger }, cancellationToken: cancellationToken));
            return rows.AsList();
        }

        internal async Task<CustomCommand> ResolveAsync(long chatId, Guid? selectedCandidateId,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var selected = selectedCandidateId == null ? null :
                await connection.QuerySingleOrDefaultAsync<CustomCommandConflictCandidate>(new CommandDefinition(@"
SELECT x.candidate_id AS CandidateId, c.telegram_chat_id AS ChatId, x.trigger AS Trigger,
x.source_trigger AS SourceTrigger, x.content AS Content, x.media_type AS MediaType,
x.file_id AS FileId, x.caption AS Caption, x.file_owner_edition AS FileOwnerEdition
FROM custom_command_conflict_candidates x JOIN groups g ON g.group_id = x.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE c.telegram_chat_id = @ChatId AND x.candidate_id = @CandidateId AND x.resolved_at IS NULL
FOR UPDATE",
                    new { ChatId = chatId, CandidateId = selectedCandidateId }, transaction,
                    cancellationToken: cancellationToken));
            if (selectedCandidateId != null && selected == null) return null;

            var trigger = selected?.Trigger;
            if (trigger == null) return null;
            var sourceTriggers = (await connection.QueryAsync<string>(new CommandDefinition(@"
SELECT x.source_trigger FROM custom_command_conflict_candidates x JOIN groups g ON g.group_id = x.group_id
JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE c.telegram_chat_id = @ChatId AND x.trigger = @Trigger AND x.resolved_at IS NULL",
                new { ChatId = chatId, Trigger = trigger }, transaction,
                cancellationToken: cancellationToken))).AsList();
            var command = new CustomCommand(chatId, trigger, selected.Content,
                LegacyPrincipalContentMapper.NormalizeCustomCommandMediaType(selected.MediaType),
                selected.FileId, selected.Caption, selected.FileOwnerEdition);
            var groupId = await EnsureGroupIdAsync(connection, transaction, chatId, cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO custom_commands (group_id, trigger, content, media_type, file_id, file_owner_edition, caption)
VALUES (@GroupId, @Trigger, @Content, @MediaType, @FileId, @FileOwnerEdition, @Caption)
ON CONFLICT (group_id, trigger) DO UPDATE SET content = EXCLUDED.content,
media_type = EXCLUDED.media_type, file_id = EXCLUDED.file_id,
file_owner_edition = EXCLUDED.file_owner_edition, caption = EXCLUDED.caption;
UPDATE custom_command_conflict_candidates SET resolved_at = now(), resolution = 'selected',
selected_candidate_id = @SelectedCandidateId
WHERE group_id = @GroupId AND trigger = @Trigger AND resolved_at IS NULL",
                new { GroupId = groupId, command.Trigger, command.Content, command.MediaType,
                    command.FileId, command.FileOwnerEdition, command.Caption,
                    SelectedCandidateId = selected.CandidateId }, transaction,
                cancellationToken: cancellationToken));
            foreach (var sourceTrigger in sourceTriggers)
                await EnqueueMirrorAsync(connection, transaction, "custom-commands", "delete-source",
                    new { chatId, trigger = sourceTrigger }, cancellationToken);
            await EnqueueMirrorAsync(connection, transaction, "custom-commands", "upsert", command,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return command;
        }

        internal async Task<bool> DeleteAllAsync(long chatId, Guid candidateId,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var conflict = await connection.QuerySingleOrDefaultAsync<(string Trigger, string[] SourceTriggers)>(
                new CommandDefinition(@"
SELECT selected.trigger AS Trigger, array_agg(all_candidates.source_trigger) AS SourceTriggers
FROM custom_command_conflict_candidates selected
JOIN custom_command_conflict_candidates all_candidates
ON all_candidates.group_id = selected.group_id AND all_candidates.trigger = selected.trigger
JOIN groups g ON g.group_id = selected.group_id JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE c.telegram_chat_id = @ChatId AND selected.candidate_id = @CandidateId
AND selected.resolved_at IS NULL AND all_candidates.resolved_at IS NULL
GROUP BY selected.trigger", new { ChatId = chatId, CandidateId = candidateId }, transaction,
                    cancellationToken: cancellationToken));
            if (conflict.Trigger == null) return false;
            await connection.ExecuteAsync(new CommandDefinition(@"
DELETE FROM custom_commands WHERE group_id = (SELECT g.group_id FROM groups g
JOIN telegram_chats c ON c.chat_id = g.chat_id WHERE c.telegram_chat_id = @ChatId) AND trigger = @Trigger;
UPDATE custom_command_conflict_candidates SET resolved_at = now(), resolution = 'deleted',
selected_candidate_id = NULL WHERE group_id = (SELECT g.group_id FROM groups g
JOIN telegram_chats c ON c.chat_id = g.chat_id WHERE c.telegram_chat_id = @ChatId)
AND trigger = @Trigger AND resolved_at IS NULL",
                new { ChatId = chatId, conflict.Trigger }, transaction, cancellationToken: cancellationToken));
            await EnqueueMirrorAsync(connection, transaction, "custom-commands", "delete",
                new { chatId, trigger = conflict.Trigger }, cancellationToken);
            foreach (var sourceTrigger in conflict.SourceTriggers)
                await EnqueueMirrorAsync(connection, transaction, "custom-commands", "delete-source",
                    new { chatId, trigger = sourceTrigger }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }

        internal async Task<bool> UndoAsync(long chatId, Guid candidateId,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await Database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var candidates = (await connection.QueryAsync<CustomCommandConflictCandidate>(new CommandDefinition(@"
SELECT x.candidate_id AS CandidateId, c.telegram_chat_id AS ChatId, x.trigger AS Trigger,
x.source_trigger AS SourceTrigger, x.content AS Content, x.media_type AS MediaType,
x.file_id AS FileId, x.caption AS Caption, x.file_owner_edition AS FileOwnerEdition
FROM custom_command_conflict_candidates selected
JOIN custom_command_conflict_candidates x ON x.group_id = selected.group_id AND x.trigger = selected.trigger
JOIN groups g ON g.group_id = x.group_id JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE c.telegram_chat_id = @ChatId AND selected.candidate_id = @CandidateId
AND selected.resolved_at IS NOT NULL AND x.resolved_at IS NOT NULL FOR UPDATE",
                new { ChatId = chatId, CandidateId = candidateId }, transaction,
                cancellationToken: cancellationToken))).AsList();
            if (candidates.Count == 0) return false;
            var trigger = candidates[0].Trigger;
            await connection.ExecuteAsync(new CommandDefinition(@"
DELETE FROM custom_commands WHERE group_id = (SELECT g.group_id FROM groups g JOIN telegram_chats c
ON c.chat_id = g.chat_id WHERE c.telegram_chat_id = @ChatId) AND trigger = @Trigger;
UPDATE custom_command_conflict_candidates SET resolved_at = NULL, resolution = NULL,
selected_candidate_id = NULL WHERE group_id = (SELECT g.group_id FROM groups g JOIN telegram_chats c
ON c.chat_id = g.chat_id WHERE c.telegram_chat_id = @ChatId) AND trigger = @Trigger",
                new { ChatId = chatId, Trigger = trigger }, transaction, cancellationToken: cancellationToken));
            await EnqueueMirrorAsync(connection, transaction, "custom-commands", "delete",
                new { chatId, trigger }, cancellationToken);
            foreach (var candidate in candidates)
                await EnqueueMirrorAsync(connection, transaction, "custom-commands", "restore-source",
                    new { chatId, trigger = candidate.SourceTrigger, candidate.Content,
                        candidate.MediaType, candidate.FileId, candidate.Caption }, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
    }
}
