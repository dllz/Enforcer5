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
| 4 | Warn history: every warn, prewarn and media warn added, removed or reset, with the balance it leaves; max-warns bans point at their warn; tempbans whose unban fails at expiry still close | as above, `warning_balances`, `warning_events` | Built |

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

### Step 4: warn history

`Helpers/WarnHistory` records every change to a warn count once Redis has made it: `/warn` and
`/prewarn` (and prewarns turned into warns at the next `/warn`, as a prewarning `remove`), the
user-menu warn button, the warn button under a report, automatic warns (flood, message and name
length, with that as the reason), media warns, `/removewarn`, `/resetwarns` and their buttons (with
the admin's reason), and the reset at max warns or the media limit. Each change is a
`warning_events` row; `warning_balances` holds the count it leaves. The balance copies the count
Redis returned instead of adding to what PostgreSQL had, so a member's balance matches Redis from
their first change on, without an import; members never touched since have no row. The lifetime
count copies Redis's `totalWarns` for `/warn`, and otherwise counts the adds seen here (Redis keeps
no total for prewarns, media warns or the user-menu button). The negative-count repair
(`Service.removeWarn0`) is housekeeping and writes no event.

Bans and warns share one ordered writer (`Helpers/HistoryWriter`), so a warn is written before the
ban it caused: a max-warns or media-limit ban sets `sanctions.trigger_warning_event_id` to the warn
that reached the limit.

Tempbans have been written since step 2. Step 4 closes one gap: when the unban at expiry fails (the
bot lost its rights, the group is gone), Redis still forgets the tempban and Telegram lifts it at
its until date anyway, so the row is closed as `completed` instead of staying `active`. Tempbans
from before step 2 are not imported; before expiry moves to PostgreSQL, check that the Redis
schedule only holds tempbans from after step 2 shipped.

Not migrated yet, still Redis only: everything else, including tempban expiry (PostgreSQL is
written, Redis decides), kicks, mutes, global bans and reports. Old Redis ban and warn data is not
imported: ban history starts when step 2 ships, warn history when step 4 does.
