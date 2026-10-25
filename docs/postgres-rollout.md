# PostgreSQL rollout

Enforcer is moving from Redis to PostgreSQL **one domain at a time**. Redis remains the store the
bot runs on throughout; each step adds a PostgreSQL feature next to it and ships, and is checked in
production, before the next one starts.

The target schema is the approved design on `feature/postgres-migration` (`docs/storage-erd.md`
there). That branch tried to migrate everything at once and is kept as the reference for table
design, the principal resolver and the lessons in its docs; its migrations are not used. Each step
here copies the tables it needs unchanged and adds them in a migration of its own.

## Setup

- `PostgresConnection` (systemd unit or `appsettings.json`) points both editions at one database.
  Empty means every PostgreSQL feature is off and the bot behaves as before.
- At startup `PostgresStorage.Start` applies pending migrations in the background (under an
  advisory lock, so the two editions do not race) and retries every minute until it succeeds. Until
  then `PostgresStorage.Ready` is false and features using it stay off. Failures go to the error
  chat.
- The database must be empty on first use. `SchemaMigrationRunner` refuses a database that holds
  Enforcer tables without EF migration history, such as a rehearsal database from the old branch.
- New migration: `dotnet tool restore`, then
  `dotnet ef migrations add <Name> -p src/Enforcer5 -o Storage/Migrations`.
  `SchemaModelTests.Migrations_MatchTheModel` fails if the model changed without one.
- PostgreSQL tests run when `ENFORCER_TEST_POSTGRES` holds a connection string (each test uses a
  throwaway schema). Without it they are skipped, as in the Docker build.
- Every step adds scenarios to the group-chat harness (`tests/Enforcer5.Tests/Harness/`, see
  CLAUDE.md "Testing"): the feature driven through real commands, checked in PostgreSQL.

## Steps

| # | Step | Tables | Status |
|---|------|--------|--------|
| 1 | Foundation: connection, schema migrations, startup | `users`, `telegram_accounts`, `telegram_account_profiles`, `user_identity_links`, `telegram_chats`, `telegram_chat_profiles`, `groups`, `sanctions` | Built |
| 2 | Ban history: every ban, tempban and unban Enforcer makes is written to `sanctions` | as above | Built |
| 3 | Principals from traffic: names, usernames, languages, group titles and links, last seen / last message, observed user count | as above | Built |

### Step 2: ban history

`Methods.BanUser`, `TempBanUser` and `UnbanUser` call `Helpers/BanHistory` once Telegram has
accepted the action, so every ban Enforcer makes is recorded: `/ban`, `/tempban`, `/unban`, the
report and user-menu buttons, max warns, the automatic flood/length/media/script bans and tempbans,
global bans being enforced, and tempbans expiring (`completed`). The writes run on one background
worker in order, retry twice, and report to the error chat if they still fail; the ban never waits
for them. Reads go through `IBanHistoryRepository.GetHistoryAsync`; nothing displays them yet.

Not recorded: kicks, mutes, channel bans (`BanChatSenderChat`), bans an admin makes in the Telegram
app directly, and unbans of bans from before the history started (there is no row to close).

### Step 3: principals from traffic

`Helpers/PrincipalObserver` records, in memory, what every message, button press and inline query
says about its accounts and chats (`Data/PrincipalObservations`), and writes it once a minute in one
transaction (`Data/PostgresPrincipalRepository`): missing accounts, users, identity links, chats and
groups are created; `last_seen_at` / `last_message_at` only move forward; a profile row is added
only when a name, username, language or title changed, closing the previous one. Shutdown flushes
too. `groups.observed_user_count` is `HLEN chat:{id}:userlast` and `public_link` is the group's
t.me username link, else the link set with /setlink (`chat:{id}links`), both read from Redis at
flush time. `active` turns false when the bot is seen leaving. Telegram's shared senders (channel
posts, anonymous admins, linked-channel forwards) are not recorded as people; a channel posting
is recorded as a chat. Measured on subprod: 2.5 s for a first batch of 20,000 accounts and 2,000
groups, 0.8 s once they exist.

Both this batch and the ban history take one advisory lock before creating accounts or links,
so they never create two users for one new account.

Not migrated yet, still Redis only: everything else, including tempban expiry, kicks, mutes,
global bans, warnings and reports. Old Redis ban data is not imported: history starts when step 2
ships.
