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
| 5 | Kicks and mutes: every kick, mute, tempmute and unmute Enforcer makes is written to `sanctions`; the user-menu warn button bans at max warns when the group says so | as above | Built |
| 6 | Reports: every report, each action an admin takes from its buttons (delete, warn, kick, ban; several per report), and who solved it; sanctions and warns taken from a report point at it | as above, `reports`, `report_actions` | Built |
| 7 | Global bans, **cut over**: PostgreSQL is the only store; `/getrekt`, `/getrektu`, `/getrekti`, `/unrekt`, `/rektlist`, `/getuser` and the check on every message use it; old Redis records imported once with SQL | as above, `global_sanctions` | Built |

### Step 2: ban history

`Methods.BanUser`, `TempBanUser` and `UnbanUser` call `Helpers/BanHistory` once Telegram has
accepted the action, so every ban Enforcer makes is recorded: `/ban`, `/tempban`, `/unban`, the
report and user-menu buttons, max warns, the automatic flood/length/media/script bans and tempbans,
global bans being enforced, and tempbans expiring (`completed`). The writes run on one background
worker in order, retry twice, and report to the error chat if they still fail; the ban never waits
for them. Reads go through `IBanHistoryRepository.GetHistoryAsync`; nothing displays them yet.

Not recorded (kicks and mutes are, from step 5): channel bans (`BanChatSenderChat`), bans an admin makes in the Telegram
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

### Step 5: kicks and mutes

`Methods.KickUser`, `MuteUser`, `TempMuteUser` and `UnmuteUser` record what Telegram accepted in
`sanctions`, with the admin who asked (null when Enforcer acted on its own) and the reason: what the
admin typed after `/kick` or `/mute`, else the replied-to message (the replied-to message for
`/tempmute`, whose text after the target is the duration); "Max warns", "Media", "Flood", "Long
message", "Name too long", "Right-to-left text", "Non-Latin script", "Reported" and "Mute On Join"
for the automatic ones. A max-warns or media-limit kick points at its warn, as bans do.

- A kick is a `kick` row closed at once (`completed`, `resolved_at` = `imposed_at`). Kicking lifts a
  ban in Telegram, so it also closes the member's open ban as `revoked`. `/kickme` is not recorded.
- A mute is a `mute` row, a tempmute a `tempmute` row with `expires_at`. A new mute closes the open
  one; `/unmute` and `/unmutenewjoiners` close it as `revoked`. Telegram lifts a tempmute by itself
  and nothing observes that, so a tempmute stays `active` with `expires_at` in the past until the
  next mute or unmute closes it as `completed` at its expiry; readers treat a past `expires_at` as
  ended.
- Not recorded: mutes lifted in the Telegram app, and members `/mutelist` finds unmuted.

The user-menu warn button ignored the group's max-warns action and always kicked (its second read
of the setting called `ToString()` on the Task, since 2017). It now bans when the group says ban,
through `Methods.BanUser` like `/warn`.

### Step 6: reports

`Helpers/ReportHistory` records a report when `/report`, `/admin` or `@admin` passes it on to the
admins (where Redis creates `flagged:{chat}:{reportMessageId}`): the reporter, the reported member
and message when the report replied to one, display names, and the time. A sender who is one of
Telegram's shared placeholders (a channel post, an anonymous admin) is kept as a display name only.
The admins' copies of the report (their chat and message ids) stay in Redis, as the approved design
has it.

The buttons under a report carry its message id as a fourth part (`banflag:{chat}:{user}:{report}`,
likewise `kickflag`, `warnflag`, `delflag`); buttons sent before step 6 fall back to the newest open
report against the member, or for delete the report on that message. Each action that succeeds is a
`report_actions` row with the admin, and a ban, kick or warn also gets `report_id` (as does a
max-warns ban or kick that a report's warn sets off). Acting does not resolve the report: the Solved
button or `/solved` does, as `resolved` / `solved` with the solver, as in Redis.

`report_actions` is not in the approved design, whose single `resolution_action` holds one action:
Daniel asked (2026-10-25) for every action on a report to be kept while Solved stays its own step.
Reports from before step 6 have no row, so actions on them and solving them write nothing.

### Step 7: global bans (cut over)

The first domain that runs on PostgreSQL alone. Global bans are a global admin's tool (one person
uses them), small (about 1,900 Redis records) and need no group data, so they moved in one step
instead of being written to both stores first.

- `global_sanctions` is copied from the approved design, plus `notified_at` (when the support chat
  was last told the banned account is there; Redis kept a `notified` field). At most one row per
  account is `active`.
- `/getrekt`, `/getrektu` and `/getrekti` write a row, with the global admin as actor; a ban on an
  account that already has one closes the old row (`revoked`, "Replaced by a new global ban").
  `/unrekt` closes the active row as `revoked` with its reason. The commands wait for the write and
  say so when it fails or PostgreSQL is not ready; nothing is written to Redis any more.
- The check on every message (`Methods.IsRekt`) reads `Helpers/GlobalBans`, an in-memory set of
  the active bans: loaded once PostgreSQL is ready, updated at once by this process's own commands,
  and reloaded every minute, so a ban made in the other edition is enforced within a minute. No
  message waits on the database. Until the first load nothing is enforced, and with no
  `PostgresConnection` global bans are off entirely (reported at startup). A failed reload keeps the
  last set.
- The support chat is told once per ban (`notified_at`), as before once per record.
- `/rektlist` lists the active bans (count and the newest 30; it never worked against Redis), and
  `/getuser` shows the member's latest ban.
- The `globalBan:{id}` hashes stay in Redis untouched as a backup, but nothing reads or writes them.
  Rolling back to a build before step 7 goes back to them, without any ban made since.

The Redis records were imported once by a SQL script generated from them (`legacy_key`
`redis:globalBan:{id}`; not in the repo, as it holds account ids), run by hand right after this
step was deployed. Their `time` / `unbanTime` fields were written with whatever culture the host
had, so the script parses the shapes the old migration branch found (month-, day- and year-first,
and the two-line "On Saturday, 22 October 2016 / At 16:09:11"); a record whose time could not be
read is still imported, with the import time. Absence means allowed, so nothing is dropped.

Not migrated yet, still Redis only: everything else, including tempban expiry (PostgreSQL is
written, Redis decides), global bans, group settings and everything read back. Old Redis ban and warn data is not
imported: ban history starts when step 2 ships, warn history when step 4 does.

## Not built yet: sharing 429s and enforcement across bot processes

Telegram's limits count per bot token. Each process keeps its own pacing and 429 pauses
(`Helpers/FloodControl`), and each edition carries out its own enforcement queue
(`Enforcement/EnforcementQueue`, journal `enforcement:{edition}:jobs`). Nothing is shared between
the editions or between processes on one token today. The seam for it is in place and a no-op:

- `Helpers/FloodSignal` (`IFloodSignal`): the gateway publishes every 429 it gets
  (`Publish(lane, chat, pausedUntil)`); a subscriber hands pauses heard from elsewhere to
  `FloodControl.ImposePause`. The intended implementation is Redis pub/sub on one channel per bot
  token, carrying lane, chat and until time (no message content).
- Cross-bot enforcement: when one edition is rate limited in a group where the other is also an
  admin, the other could carry out its jobs. The intended shape is a Redis stream per group (or one
  shared stream with a consumer group per edition), with jobs in the journal's JSON form, claimed
  with `XAUTOCLAIM` after a timeout so a job is carried out by exactly one edition. That needs
  Redis 6.2 or later; check the server version first. The journal stays the source of truth until
  then.

This belongs to the PostgreSQL branch, where the two editions already share a database and start
sharing state; it is not required by anything built so far.
