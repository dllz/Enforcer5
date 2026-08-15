using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Enforcer5.Storage.Loading
{
    /// <summary>
    /// Creates missing principals for a whole batch in a handful of statements.
    /// <para>
    /// The row-at-a-time <see cref="PrincipalResolver"/> is correct for the runtime hot path, where
    /// one message resolves one user. It is the wrong shape for a load: every call takes an advisory
    /// lock and issues four to six round trips, so the operations backfill spends more time
    /// resolving principals than writing data - at 7.38M group statistics rows that is over ten
    /// million round trips before a single counter is stored, which is why that domain has never
    /// completed a run.
    /// </para>
    /// <para>
    /// Identities are assigned from reserved sequence blocks rather than read back per row. One
    /// <c>setval</c> claims a contiguous range, a window function hands the range out, and every
    /// dependent table is then a single set-based insert. This is what makes an entire tier a
    /// handful of statements instead of a loop.
    /// </para>
    /// </summary>
    internal static class BulkPrincipalResolver
    {
        /// <summary>
        /// Ensures a <c>users</c>, <c>telegram_accounts</c> and <c>user_identity_links</c> row exists
        /// for every Telegram user id the source query returns.
        /// </summary>
        /// <param name="sourceSql">
        /// A query returning one bigint column of Telegram user ids. Internal callers pass literal
        /// SQL naming a staging table; no external value is ever interpolated.
        /// </param>
        /// <returns>How many principals were created.</returns>
        internal static async Task<long> EnsureAccountsAsync(NpgsqlConnection connection,
            NpgsqlTransaction transaction, string sourceSql, CancellationToken cancellationToken = default)
        {
            await ExecuteAsync(connection, transaction, $@"
CREATE TEMP TABLE bulk_missing_accounts ON COMMIT DROP AS
SELECT DISTINCT source.telegram_user_id
  FROM ({sourceSql}) AS source(telegram_user_id)
 WHERE source.telegram_user_id IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM telegram_accounts a
                    WHERE a.telegram_user_id = source.telegram_user_id)",
                cancellationToken);

            var missing = await ScalarAsync<long>(connection, transaction,
                "SELECT count(*) FROM bulk_missing_accounts", cancellationToken);
            if (missing == 0) return 0;

            // Reserved against the count of what is actually missing, so the block is exact and
            // leaves no gaps - the prototype reserved against the full staging count and skipped
            // however many users already existed.
            var userBase = await ReserveAsync(connection, transaction, "users", "user_id", missing,
                cancellationToken);
            var accountBase = await ReserveAsync(connection, transaction, "telegram_accounts", "account_id",
                missing, cancellationToken);

            await ExecuteAsync(connection, transaction, $@"
CREATE TEMP TABLE bulk_assigned_accounts ON COMMIT DROP AS
SELECT telegram_user_id,
       {userBase} + (row_number() OVER (ORDER BY telegram_user_id)) - 1 AS user_id,
       {accountBase} + (row_number() OVER (ORDER BY telegram_user_id)) - 1 AS account_id
  FROM bulk_missing_accounts;

INSERT INTO users (user_id, created_at, updated_at)
OVERRIDING SYSTEM VALUE
SELECT user_id, now(), now() FROM bulk_assigned_accounts;

INSERT INTO telegram_accounts (account_id, telegram_user_id, is_bot, first_seen_at, last_seen_at)
OVERRIDING SYSTEM VALUE
SELECT account_id, telegram_user_id, false, now(), now() FROM bulk_assigned_accounts;

INSERT INTO user_identity_links (user_id, account_id, source, reason, linked_at)
SELECT user_id, account_id, 'migration', 'backfill', now() FROM bulk_assigned_accounts;",
                cancellationToken);

            return missing;
        }

        /// <summary>
        /// Ensures a <c>telegram_chats</c> and <c>groups</c> row exists for every Telegram chat id the
        /// source query returns.
        /// <para>
        /// Chat type is recorded as <c>unknown</c> rather than guessed. The legacy data does not
        /// carry it, and <see cref="PrincipalResolver.EnsureChatAsync"/> already treats
        /// <c>unknown</c> as "do not overwrite", so a later observation from Telegram corrects it.
        /// </para>
        /// </summary>
        internal static async Task<long> EnsureGroupsAsync(NpgsqlConnection connection,
            NpgsqlTransaction transaction, string sourceSql, CancellationToken cancellationToken = default)
        {
            await ExecuteAsync(connection, transaction, $@"
CREATE TEMP TABLE bulk_missing_chats ON COMMIT DROP AS
SELECT DISTINCT source.telegram_chat_id
  FROM ({sourceSql}) AS source(telegram_chat_id)
 WHERE source.telegram_chat_id IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM telegram_chats c
                    WHERE c.telegram_chat_id = source.telegram_chat_id)",
                cancellationToken);

            var missingChats = await ScalarAsync<long>(connection, transaction,
                "SELECT count(*) FROM bulk_missing_chats", cancellationToken);
            if (missingChats > 0)
            {
                var chatBase = await ReserveAsync(connection, transaction, "telegram_chats", "chat_id",
                    missingChats, cancellationToken);
                await ExecuteAsync(connection, transaction, $@"
INSERT INTO telegram_chats (chat_id, telegram_chat_id, chat_type, first_seen_at, last_seen_at)
OVERRIDING SYSTEM VALUE
SELECT {chatBase} + (row_number() OVER (ORDER BY telegram_chat_id)) - 1,
       telegram_chat_id, 'unknown', now(), now()
  FROM bulk_missing_chats;", cancellationToken);
            }

            // Separate pass: a chat can exist without a group, because telegram_chats also holds
            // log-chat destinations that were never groups in their own right.
            await ExecuteAsync(connection, transaction, $@"
CREATE TEMP TABLE bulk_missing_groups ON COMMIT DROP AS
SELECT c.chat_id
  FROM telegram_chats c
 WHERE c.telegram_chat_id IN (SELECT source.telegram_chat_id
                                FROM ({sourceSql}) AS source(telegram_chat_id)
                               WHERE source.telegram_chat_id IS NOT NULL)
   AND NOT EXISTS (SELECT 1 FROM groups g WHERE g.chat_id = c.chat_id)",
                cancellationToken);

            var missingGroups = await ScalarAsync<long>(connection, transaction,
                "SELECT count(*) FROM bulk_missing_groups", cancellationToken);
            if (missingGroups == 0) return 0;

            var groupBase = await ReserveAsync(connection, transaction, "groups", "group_id", missingGroups,
                cancellationToken);
            await ExecuteAsync(connection, transaction, $@"
INSERT INTO groups (group_id, chat_id, active, created_at, updated_at)
OVERRIDING SYSTEM VALUE
SELECT {groupBase} + (row_number() OVER (ORDER BY chat_id)) - 1, chat_id, TRUE, now(), now()
  FROM bulk_missing_groups;", cancellationToken);

            return missingGroups;
        }

        /// <summary>
        /// Claims a contiguous block of generated ids and returns its first value.
        /// </summary>
        private static Task<long> ReserveAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
            string table, string column, long count, CancellationToken cancellationToken) =>
            ScalarAsync<long>(connection, transaction,
                $"SELECT setval(pg_get_serial_sequence('{table}','{column}'), " +
                $"coalesce((SELECT max({column}) FROM {table}), 0) + @count) - @count + 1",
                cancellationToken, ("count", count));

        private static async Task ExecuteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
            string sql, CancellationToken cancellationToken)
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
    }
}
