using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Dapper;

namespace Enforcer5.Storage
{
    internal static class PrincipalResolver
    {
        internal sealed record UserIds(long AccountId, long UserId);
        internal sealed record GroupIds(long ChatId, long GroupId);

        internal static async Task<UserIds> EnsureUserAsync(DbConnection connection,
            DbTransaction transaction, long telegramUserId, string displayName = null,
            string username = null, bool isBot = false,
            CancellationToken cancellationToken = default)
        {
            await LockAsync(connection, transaction, "user", telegramUserId, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var accountId = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
                "SELECT account_id FROM telegram_accounts WHERE telegram_user_id = @TelegramUserId",
                new { TelegramUserId = telegramUserId }, transaction, cancellationToken: cancellationToken));
            if (!accountId.HasValue)
                accountId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO telegram_accounts (telegram_user_id, is_bot, first_seen_at, last_seen_at)
VALUES (@TelegramUserId, @IsBot, @Now, @Now) RETURNING account_id",
                    new { TelegramUserId = telegramUserId, IsBot = isBot, Now = now }, transaction,
                    cancellationToken: cancellationToken));
            else
                await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE telegram_accounts SET is_bot = @IsBot, last_seen_at = @Now WHERE account_id = @AccountId",
                    new { AccountId = accountId.Value, IsBot = isBot, Now = now }, transaction,
                    cancellationToken: cancellationToken));

            var userId = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(@"
SELECT user_id FROM user_identity_links WHERE account_id = @AccountId AND unlinked_at IS NULL",
                new { AccountId = accountId.Value }, transaction, cancellationToken: cancellationToken));
            if (!userId.HasValue)
            {
                userId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO users (created_at, updated_at) VALUES (@Now, @Now) RETURNING user_id",
                    new { Now = now }, transaction, cancellationToken: cancellationToken));
                await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO user_identity_links (user_id, account_id, source, reason, linked_at)
VALUES (@UserId, @AccountId, 'telegram', 'observed', @Now)",
                    new { UserId = userId.Value, AccountId = accountId.Value, Now = now }, transaction,
                    cancellationToken: cancellationToken));
            }
            else
                await connection.ExecuteAsync(new CommandDefinition(
                    "UPDATE users SET updated_at = @Now WHERE user_id = @UserId",
                    new { UserId = userId.Value, Now = now }, transaction,
                    cancellationToken: cancellationToken));

            if (displayName != null || username != null)
            {
                var current = await connection.QuerySingleOrDefaultAsync<AccountProfile>(new CommandDefinition(@"
SELECT profile_id AS ProfileId, display_name AS DisplayName, username AS Username
FROM telegram_account_profiles WHERE account_id = @AccountId AND valid_to IS NULL FOR UPDATE",
                    new { AccountId = accountId.Value }, transaction, cancellationToken: cancellationToken));
                if (current == null || !string.Equals(current.DisplayName, displayName, StringComparison.Ordinal) ||
                    !string.Equals(current.Username, username, StringComparison.OrdinalIgnoreCase))
                {
                    if (current != null)
                        await connection.ExecuteAsync(new CommandDefinition(
                            "UPDATE telegram_account_profiles SET valid_to = @Now WHERE profile_id = @ProfileId",
                            new { Now = now, current.ProfileId }, transaction,
                            cancellationToken: cancellationToken));
                    await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO telegram_account_profiles (account_id, display_name, username, valid_from)
VALUES (@AccountId, @DisplayName, @Username, @Now)",
                        new { AccountId = accountId.Value, DisplayName = displayName, Username = username, Now = now },
                        transaction, cancellationToken: cancellationToken));
                }
            }

            return new UserIds(accountId.Value, userId.Value);
        }

        internal static async Task<GroupIds> EnsureGroupAsync(DbConnection connection,
            DbTransaction transaction, long telegramChatId, string title = null, string username = null,
            string chatType = "group", CancellationToken cancellationToken = default)
        {
            await LockAsync(connection, transaction, "group", telegramChatId, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var chatId = await EnsureChatAsync(connection, transaction, telegramChatId,
                NormalizeChatType(chatType), cancellationToken);

            var groupId = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
                "SELECT group_id FROM groups WHERE chat_id = @ChatId", new { ChatId = chatId },
                transaction, cancellationToken: cancellationToken));
            if (!groupId.HasValue)
                groupId = await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO groups (chat_id, title, username, active, created_at, updated_at)
VALUES (@ChatId, @Title, @Username, TRUE, @Now, @Now) RETURNING group_id",
                    new { ChatId = chatId, Title = title, Username = username, Now = now }, transaction,
                    cancellationToken: cancellationToken));
            else
                await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE groups SET title = COALESCE(@Title, title), username = COALESCE(@Username, username),
active = TRUE, updated_at = @Now WHERE group_id = @GroupId",
                    new { GroupId = groupId.Value, Title = title, Username = username, Now = now }, transaction,
                    cancellationToken: cancellationToken));

            if (title != null || username != null)
            {
                var current = await connection.QuerySingleOrDefaultAsync<ChatProfile>(new CommandDefinition(@"
SELECT chat_profile_id AS ProfileId, title AS Title, username AS Username
FROM telegram_chat_profiles WHERE chat_id = @ChatId AND valid_to IS NULL FOR UPDATE",
                    new { ChatId = chatId }, transaction, cancellationToken: cancellationToken));
                if (current == null || !string.Equals(current.Title, title, StringComparison.Ordinal) ||
                    !string.Equals(current.Username, username, StringComparison.OrdinalIgnoreCase))
                {
                    if (current != null)
                        await connection.ExecuteAsync(new CommandDefinition(
                            "UPDATE telegram_chat_profiles SET valid_to = @Now WHERE chat_profile_id = @ProfileId",
                            new { Now = now, current.ProfileId }, transaction,
                            cancellationToken: cancellationToken));
                    await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO telegram_chat_profiles (chat_id, title, username, valid_from)
VALUES (@ChatId, @Title, @Username, @Now)",
                        new { ChatId = chatId, Title = title, Username = username, Now = now }, transaction,
                        cancellationToken: cancellationToken));
                }
            }

            return new GroupIds(chatId, groupId.Value);
        }

        internal static async Task<long> EnsureChatAsync(DbConnection connection, DbTransaction transaction,
            long telegramChatId, string chatType = "unknown", CancellationToken cancellationToken = default)
        {
            await LockAsync(connection, transaction, "chat", telegramChatId, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var chatId = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
                "SELECT chat_id FROM telegram_chats WHERE telegram_chat_id = @TelegramChatId",
                new { TelegramChatId = telegramChatId }, transaction, cancellationToken: cancellationToken));
            var normalized = NormalizeChatType(chatType);
            if (!chatId.HasValue)
                return await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO telegram_chats (telegram_chat_id, chat_type, first_seen_at, last_seen_at)
VALUES (@TelegramChatId, @ChatType, @Now, @Now) RETURNING chat_id",
                    new { TelegramChatId = telegramChatId, ChatType = normalized, Now = now }, transaction,
                    cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE telegram_chats SET chat_type = CASE WHEN @ChatType = 'unknown' THEN chat_type ELSE @ChatType END,
last_seen_at = @Now WHERE chat_id = @ChatId",
                new { ChatId = chatId.Value, ChatType = normalized, Now = now }, transaction,
                cancellationToken: cancellationToken));
            return chatId.Value;
        }

        internal static Task<long?> ResolveGroupIdAsync(DbConnection connection, long telegramChatId,
            DbTransaction transaction = null, CancellationToken cancellationToken = default) =>
            connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(@"
SELECT g.group_id FROM groups g JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE c.telegram_chat_id = @TelegramChatId", new { TelegramChatId = telegramChatId }, transaction,
                cancellationToken: cancellationToken));

        private static Task LockAsync(DbConnection connection, DbTransaction transaction, string kind,
            long externalId, CancellationToken cancellationToken) => connection.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_xact_lock(hashtext(@Kind), hashtext(@ExternalId))",
                new { Kind = kind, ExternalId = externalId.ToString() }, transaction,
                cancellationToken: cancellationToken));

        private static string NormalizeChatType(string value) => value?.ToLowerInvariant() switch
        {
            "private" => "private", "group" => "group", "supergroup" => "supergroup",
            "channel" => "channel", _ => "unknown"
        };

        private sealed class AccountProfile { public long ProfileId { get; set; } public string DisplayName { get; set; } public string Username { get; set; } }
        private sealed class ChatProfile { public long ProfileId { get; set; } public string Title { get; set; } public string Username { get; set; } }
    }
}
