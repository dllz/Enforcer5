using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage;
using Npgsql;

namespace Enforcer5.Data
{
    /// <summary>Writes what the bot has seen of accounts and chats.</summary>
    internal interface IPrincipalRepository
    {
        Task ApplyAsync(ObservationBatch batch, DateTimeOffset now);
    }

    /// <summary>
    /// Keeps the principal tables current from observed traffic, one set-based transaction per
    /// batch: accounts, their users and identity links, chats and groups are created when missing;
    /// last_seen_at / last_message_at only move forward; a profile row (name, username, language,
    /// group title) is added only when something changed, closing the previous one, so the profile
    /// tables are the history of names.
    /// </summary>
    internal sealed class PostgresPrincipalRepository : IPrincipalRepository
    {
        /// <summary>
        /// Taken by every transaction that may create accounts or identity links (this batch and the
        /// ban history), so the two never both create a link for the same new account.
        /// </summary>
        internal const long PrincipalLock = 0x454E465052494E43; // "ENFPRINC"

        private readonly Func<PostgresDatabase> _database;

        internal PostgresPrincipalRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        public async Task ApplyAsync(ObservationBatch batch, DateTimeOffset now)
        {
            if (batch.IsEmpty) return;
            var database = _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");
            await using var connection = await database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(@PrincipalLock)", new { PrincipalLock }, transaction);
            var at = now.UtcDateTime;
            if (batch.Accounts.Count > 0) await ApplyAccountsAsync(connection, transaction, batch, at);
            if (batch.Chats.Count > 0) await ApplyChatsAsync(connection, transaction, batch, at);
            await transaction.CommitAsync();
        }

        private static DateTime? Utc(DateTimeOffset? value) => value?.UtcDateTime;

        private static async Task ApplyAccountsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
            ObservationBatch batch, DateTime now)
        {
            var a = batch.Accounts;
            await connection.ExecuteAsync(@"
CREATE TEMP TABLE obs_accounts (telegram_user_id bigint PRIMARY KEY, is_bot boolean NOT NULL, display_name text,
    username text, language_code text, last_seen_at timestamptz) ON COMMIT DROP;
INSERT INTO obs_accounts
SELECT * FROM unnest(@Ids, @Bots, @Names, @Usernames, @Languages, @Seen);",
                new
                {
                    Ids = a.Select(x => x.TelegramUserId).ToArray(),
                    Bots = a.Select(x => x.IsBot).ToArray(),
                    Names = a.Select(x => x.DisplayName).ToArray(),
                    Usernames = a.Select(x => x.Username).ToArray(),
                    Languages = a.Select(x => x.LanguageCode).ToArray(),
                    Seen = a.Select(x => Utc(x.LastSeenAt)).ToArray()
                }, transaction);

            await connection.ExecuteAsync(@"
INSERT INTO telegram_accounts (telegram_user_id, is_bot, first_seen_at, last_seen_at)
SELECT telegram_user_id, is_bot, COALESCE(last_seen_at, @Now), COALESCE(last_seen_at, @Now) FROM obs_accounts
ON CONFLICT (telegram_user_id) DO UPDATE SET is_bot = EXCLUDED.is_bot
WHERE telegram_accounts.is_bot IS DISTINCT FROM EXCLUDED.is_bot;

UPDATE telegram_accounts t SET last_seen_at = o.last_seen_at
FROM obs_accounts o
WHERE t.telegram_user_id = o.telegram_user_id AND o.last_seen_at > t.last_seen_at;

-- A user and identity link for every account that has none. The user ids are drawn first so
-- each new user can be paired with its account in one statement.
WITH missing AS (
    SELECT t.account_id, nextval(pg_get_serial_sequence('users', 'user_id')) AS user_id
    FROM obs_accounts o JOIN telegram_accounts t USING (telegram_user_id)
    WHERE NOT EXISTS (SELECT 1 FROM user_identity_links l WHERE l.account_id = t.account_id AND l.unlinked_at IS NULL)
), new_users AS (
    INSERT INTO users (user_id, created_at, updated_at) OVERRIDING SYSTEM VALUE
    SELECT user_id, @Now, @Now FROM missing
)
INSERT INTO user_identity_links (user_id, account_id, source, reason, linked_at)
SELECT user_id, account_id, 'telegram', 'observed', @Now FROM missing;

-- Profiles: a new row only when the name, username or language changed. Telegram leaves the
-- language out of many updates, so a missing one keeps the stored one.
CREATE TEMP TABLE changed_profiles ON COMMIT DROP AS
SELECT t.account_id, p.profile_id, o.display_name, o.username,
       COALESCE(o.language_code, p.language_code) AS language_code,
       CASE WHEN p.profile_id IS NULL THEN @Now
            ELSE GREATEST(@Now, p.valid_from + interval '1 microsecond') END AS switch_at
FROM obs_accounts o
JOIN telegram_accounts t USING (telegram_user_id)
LEFT JOIN telegram_account_profiles p ON p.account_id = t.account_id AND p.valid_to IS NULL
WHERE o.display_name IS NOT NULL
  AND (p.profile_id IS NULL
       OR p.display_name IS DISTINCT FROM o.display_name
       OR lower(p.username) IS DISTINCT FROM lower(o.username)
       OR (o.language_code IS NOT NULL AND p.language_code IS DISTINCT FROM o.language_code));

UPDATE telegram_account_profiles p SET valid_to = c.switch_at
FROM changed_profiles c WHERE p.profile_id = c.profile_id;

INSERT INTO telegram_account_profiles (account_id, display_name, username, language_code, valid_from)
SELECT account_id, display_name, username, language_code, switch_at FROM changed_profiles;",
                new { Now = now }, transaction);
        }

        private static async Task ApplyChatsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
            ObservationBatch batch, DateTime now)
        {
            var c = batch.Chats;
            await connection.ExecuteAsync(@"
CREATE TEMP TABLE obs_chats (telegram_chat_id bigint PRIMARY KEY, chat_type text NOT NULL, title text, username text,
    last_message_at timestamptz, active boolean, observed_user_count bigint, link_known boolean NOT NULL,
    public_link text) ON COMMIT DROP;
INSERT INTO obs_chats
SELECT * FROM unnest(@Ids, @Types, @Titles, @Usernames, @Last, @Active, @Counts, @LinkKnown, @Links);",
                new
                {
                    Ids = c.Select(x => x.TelegramChatId).ToArray(),
                    Types = c.Select(x => x.ChatType).ToArray(),
                    Titles = c.Select(x => x.Title).ToArray(),
                    Usernames = c.Select(x => x.Username).ToArray(),
                    Last = c.Select(x => Utc(x.LastMessageAt)).ToArray(),
                    Active = c.Select(x => x.Active).ToArray(),
                    Counts = c.Select(x => x.ObservedUserCount).ToArray(),
                    LinkKnown = c.Select(x => x.LinkKnown).ToArray(),
                    Links = c.Select(x => x.PublicLink).ToArray()
                }, transaction);

            await connection.ExecuteAsync(@"
INSERT INTO telegram_chats (telegram_chat_id, chat_type, first_seen_at, last_seen_at)
SELECT telegram_chat_id, chat_type, COALESCE(last_message_at, @Now), COALESCE(last_message_at, @Now) FROM obs_chats
ON CONFLICT (telegram_chat_id) DO UPDATE SET chat_type = EXCLUDED.chat_type
WHERE EXCLUDED.chat_type <> 'unknown' AND telegram_chats.chat_type IS DISTINCT FROM EXCLUDED.chat_type;

UPDATE telegram_chats t SET last_seen_at = o.last_message_at
FROM obs_chats o
WHERE t.telegram_chat_id = o.telegram_chat_id AND o.last_message_at > t.last_seen_at;

INSERT INTO groups (chat_id, title, username, active, created_at, updated_at)
SELECT t.chat_id, o.title, o.username, COALESCE(o.active, TRUE), @Now, @Now
FROM obs_chats o JOIN telegram_chats t USING (telegram_chat_id)
WHERE o.chat_type IN ('group', 'supergroup')
ON CONFLICT (chat_id) DO NOTHING;

UPDATE groups g SET
    title = COALESCE(o.title, g.title),
    username = CASE WHEN o.title IS NOT NULL THEN o.username ELSE g.username END,
    public_link = CASE WHEN o.link_known THEN o.public_link ELSE g.public_link END,
    active = COALESCE(o.active, g.active),
    observed_user_count = COALESCE(o.observed_user_count, g.observed_user_count),
    last_message_at = GREATEST(g.last_message_at, o.last_message_at),
    updated_at = @Now
FROM obs_chats o JOIN telegram_chats t USING (telegram_chat_id)
WHERE g.chat_id = t.chat_id AND o.chat_type IN ('group', 'supergroup');

-- Title and username history for groups and channels, a new row only on a change.
CREATE TEMP TABLE changed_chat_profiles ON COMMIT DROP AS
SELECT t.chat_id, p.chat_profile_id, o.title, o.username,
       CASE WHEN p.chat_profile_id IS NULL THEN @Now
            ELSE GREATEST(@Now, p.valid_from + interval '1 microsecond') END AS switch_at
FROM obs_chats o
JOIN telegram_chats t USING (telegram_chat_id)
LEFT JOIN telegram_chat_profiles p ON p.chat_id = t.chat_id AND p.valid_to IS NULL
WHERE o.title IS NOT NULL AND o.chat_type <> 'private'
  AND (p.chat_profile_id IS NULL
       OR p.title IS DISTINCT FROM o.title
       OR lower(p.username) IS DISTINCT FROM lower(o.username));

UPDATE telegram_chat_profiles p SET valid_to = c.switch_at
FROM changed_chat_profiles c WHERE p.chat_profile_id = c.chat_profile_id;

INSERT INTO telegram_chat_profiles (chat_id, title, username, valid_from)
SELECT chat_id, title, username, switch_at FROM changed_chat_profiles;",
                new { Now = now }, transaction);
        }
    }
}
