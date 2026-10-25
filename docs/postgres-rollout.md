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

## Steps

| # | Step | Tables | Status |
|---|------|--------|--------|
| 1 | Foundation: connection, schema migrations, startup | `users`, `telegram_accounts`, `telegram_account_profiles`, `user_identity_links`, `telegram_chats`, `telegram_chat_profiles`, `groups`, `sanctions` | Built |
| 2 | Ban history: every ban, tempban and unban Enforcer makes is written to `sanctions` | as above | Built |

Not migrated yet, still Redis only: everything else, including tempban expiry, kicks, mutes,
global bans, warnings and reports. Old Redis ban data is not imported: history starts when step 2
ships.
