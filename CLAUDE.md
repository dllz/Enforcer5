# Enforcer — Telegram group management bot

Single .NET 10 console app. Redis is the main datastore; PostgreSQL is being rolled in one domain at a time, and global bans and the automatic checks' settings already live only there (`docs/postgres-rollout.md`). New data goes to PostgreSQL; Redis is a cache and a communication system, not a store (Daniel, 2026-10-27). Deployed on Linux under systemd,
alongside blackwolf, and managed by **blackwolf's DeployBot** (see below).

## Build

```bash
export PATH="/Users/daniel.lotz/.dotnet:$PATH" && dotnet build Enforcer5.slnx -c Release
```

Build configs: `Debug`, `Release` (→ normal bot), `Premium` (→ premium bot).
Note the solution is `.slnx` (the SDK 10 format), not `.sln`.

Two bots are produced from one codebase via `DefineConstants` + per-config `AssemblyName`,
mirroring blackwolf:

| Config | Constant | Assembly | Token key | Image tag |
|---|---|---|---|---|
| `Release` | `NORMAL` | `Enforcer.dll` | `EnforcerAPI` | `enforcer-normal` |
| `Premium` | `PREMIUM` | `Enforcer Premium.dll` | `EnforcerPremiumAPI` | `enforcer-premium` |

`Debug` defines `NORMAL` too. Anything guarded by `#if PREMIUM` only exists in the premium
build — check both configs compile before finishing work.

---

## Code Style

We want code to always be maintainable, testable and an improvement on the code quality we
found. Refactoring to improve as we make changes.

This codebase predates that standard in places: static-everything, ~330 blocking `.Result`
calls, and broad `catch` blocks. That structure was deliberately **kept** during the .NET 10
migration to keep the diff reviewable, matching blackwolf. Improve it incrementally where
you are already working; don't start a global rewrite.

---

## Project Map

| Path | Purpose |
|---|---|
| `src/Enforcer5/Program.cs` | Entry point. Applies staged updates, starts Redis, then background threads |
| `src/Enforcer5/Helpers/Utilities.cs` | `Bot` (Telegram client, send helpers, config-backed paths) and `Redis` |
| `src/Enforcer5/Helpers/RegHelper.cs` | Config gateway — env vars win over `appsettings.json` |
| `src/Enforcer5/Helpers/LogHelper.cs` | Rotating file log (10MB + one `.old`) written to the tmpfs `LogPath` |
| `src/Enforcer5/Helpers/Updater.cs` | Staged-update support for DeployBot |
| `src/Enforcer5/Helpers/MessageExtensions.cs` | Bridges 13.x shapes 22.x dropped (`NewChatMember`, `IsServiceMessage`) |
| `src/Enforcer5/Handlers/UpdateHandler.cs` | Update routing, command dispatch |
| `src/Enforcer5/Helpers/TelegramGateway.cs`, `FloodControl.cs` | Every Telegram call: pacing under Telegram's limits and 429 handling, per lane (see Rate limits) |
| `src/Enforcer5/Helpers/Clock.cs` | Time for the new code; simulated in the harness |
| `src/Enforcer5/Enforcement/` | The enforcement queue: every ban, kick, mute, unban, delete and permission change |
| `src/Enforcer5/Helpers/CommandLimiter.cs`, `CommandGate.cs` | Command, button and @admin limits and the slow lane |
| `src/Enforcer5/Raids/` | Raid detection, raid mode and raid telemetry; `Helpers/RaidCapture`, `MemberEvents`, `MemberTrust` feed it |
| `src/Enforcer5/Data/` | Repository interfaces, their Redis and PostgreSQL implementations, and `Repositories` (the composition root) |
| `src/Enforcer5/Detection/` | Detection v2 (`docs/detection-design.md`). So far: each group's settings of the automatic checks as one `DetectionPolicy`, from PostgreSQL through `DetectionSettings` (memory, Redis cache), imported once from the old hashes by `LegacySettings`; the engine (`DetectionEngine`: signs from `SignCatalog`, scores, observe mode, `detections`); and `Decisions`, which records every decision about a member (bot, admin command or button, Telegram app) as a label in `member_labels`, the data Detection learns a group's rules from |
| `src/Enforcer5/Storage/` | PostgreSQL: connection, EF Core schema model and migrations, startup migration runner |
| `src/Enforcer5/Commands/` | Commands, callbacks, inline queries, menus |
| `src/Enforcer5/Attributes/` | `[Command]`, `[Callback]`, `[Query]` — bound by reflection at startup |
| `src/Enforcer5/Languages/` | XML localisation, copied to output |
| `deploy/` | `setup.sh`, `enf.sh`, systemd unit templates |
| `tests/Enforcer5.Tests/` | MSTest + Moq unit tests; run by the Docker build (see Testing) |

### Command dispatch

`Bot.Initialize()` reflects over `Commands`, `CallBacks` and `Queries` looking for
`[Command]` / `[Callback]` / `[Query]` attributes and binds delegates. **This blocks
trimming and NativeAOT** — `PublishTrimmed` is off deliberately. A new command is just a
new attributed static method; nothing needs registering.

The command menus Telegram shows are set by the bot at startup (`Helpers/CommandMenus.cs`, as in
blackwolf), not in BotFather, which they overwrite. A command in a public menu needs a short
`cmd{trigger}` description in `English.xml` (a test checks); staff commands take the attribute's
`Description`; `Menu = CommandMenu.…` places the exceptions (hidden, private only, admins).

---

## Configuration

All config comes from `RegHelper`. Keys are PascalCase and identical as JSON keys and env
vars, so a systemd `Environment=EnforcerAPI=...` line and an `appsettings.json` entry are
interchangeable.

**Env vars take precedence over `appsettings.json`.** This is a deliberate divergence from
blackwolf, where JSON wins and a stray file can silently override the unit file.

Copy `appsettings.template.json` → `appsettings.json` for local dev (gitignored).
Production ships no JSON file; everything comes from the systemd unit.

| Key | Notes |
|---|---|
| `EnforcerAPI` / `EnforcerPremiumAPI` | Bot token; which one is read is a compile-time choice |
| `TelegramServerUrl` | Optional self-hosted Bot API server |
| `TelegramFileUrl` | Optional. Where files sent to the bot are downloaded from, as `{TelegramFileUrl}/{token}/{file_path}`. Unset with a self-hosted server, it defaults to that server's host on port 80 plus `/file` (e.g. `http://192.168.0.51/file`); our Bot API server answers 404 on its own `/file/bot{token}/` route, and blackwolf uses the same port-80 address. Unset without a server, the official API is used |
| `RedisConnection` / `RedisPassword` | Shared instance with blackwolf; DB index 0 |
| `PostgresConnection` | Optional. One database shared by both editions; empty turns every PostgreSQL feature off. See `docs/postgres-rollout.md` |
| `PaymentProviderToken` | Telegram Payments. Never commit this |
| `ErrorChatId` | Errors and startup notices; falls back to `Constants.Devs[0]` |
| `LanguagesPath` | Live XML directory. Both production editions use `/opt/enforcer/Languages` |
| `TempLanguageFilesPath` | Per-edition upload staging; relative paths resolve against the app dir |
| `LogPath` | Per-edition rotating logs; relative paths resolve against the app dir |
| `DisplayTimeZone` | Display formatting only, default `Europe/Amsterdam` |

---

## Time handling

Split these two and do not conflate them:

- **Logic** (tempban expiry, `untilDate` sent to Telegram) — always pure UTC.
- **Display** — `Methods.DisplayNow()`, backed by `DisplayTimeZone`.

The pre-migration code used `DateTime.UtcNow.AddHours(2)` on both the write and read side
of tempbans. Those cancelled out, so the internal bookkeeping was fine; the bug was that
the same value went to Telegram's `untilDate`, which Telegram reads as UTC. Before
"fixing" any remaining offset, check whether a matching one exists on the other side.

---

## Telegram.Bot 22.x

Migrated from a private 13.x fork (`Telegram.Bot.Enforcer`). Things that bite:

- Long polling is `IUpdateHandler` + `ReceiverOptions`, **not** the `bot.OnMessage +=`
  event API — the offset is persisted in Redis (`bot:last_update` /
  `bot:last_Premium_update`) so restarts neither reprocess nor skip, and the simple event
  API gives no control over the starting offset.
- Methods dropped the `Async` suffix; several renamed (`SendTextMessage`→`SendMessage`,
  `KickChatMember`→`BanChatMember`, `GetFile`→`GetInfoAndDownloadFile`).
- Mutating calls return non-generic `Task` and **throw** instead of returning `false`.
- `replyToMessageId:` → `replyParameters:`, `disableWebPagePreview:` → `linkPreviewOptions:`.
- `Message.NewChatMember` (singular) and `MessageType.ServiceMessage` are gone —
  `MessageExtensions` provides both back as C# 14 extension members.
- Error handling uses typed `ApiRequestException` (`ErrorCode`, `Parameters.RetryAfter`),
  not message-string matching. Blocking calls wrap it in `AggregateException`, so use
  `Bot.AsApiError(e)` to unwrap.
- A post made as a channel has `SenderChat` = the channel and `From` = Telegram's shared
  placeholder `136817688`; the member behind it is never visible to bots. Act on the channel
  (`BanChatSenderChat`), never on `From`. Anonymous admins have `SenderChat` = the group itself.
  See `Models/ChannelPosts.cs`.
- Messages from other bots (Bot-to-Bot Communication Mode) are dropped on arrival except from trusted
  bots (`Helpers/BotSenderFilter.cs`: Esme, GroupAnonymousBot, Channel_Bot). Esme may run
  `/uploadlanguage` without being in `langAdmins`; nothing else. `/cmd@bot` is matched without case.

---

## Rate limits, enforcement and raids

A raid can get a group deleted, so enforcement must never fail and Telegram must never be given a
reason to refuse it. Four pieces, outermost first:

- **`TelegramGateway`** wraps `Bot.Api`, so every call (the ~250 direct ones too) goes through
  `FloodControl`. Lanes: *messages* (send/edit/forward/copy) are paced to 3 per 3s per chat, 20 a
  minute per group and 25 a second bot-wide, below Telegram's 30, so moderation always has room; a
  message whose slot is more than 10s away, or whose chat is paused longer than that, fails at once
  with a 429 (`LocalRateLimitException`). *Moderation* (ban, restrict, delete, permissions) has its
  own 25 a second and never waits behind a pause: after a 429 it fails at once and the queue retries
  it. *Answers* to buttons and inline queries wait at most 3s. A 429 pauses that lane in that chat
  for its `retry_after`. Never `Thread.Sleep` a 429 again: that held enforcement permits for a minute.
- **`Announcements`**: a plain group message that hits a 429 goes out merged with the others once the
  pause is over. Messages with buttons and private ones are dropped.
- **`EnforcementQueue`**: every enforcement action is a job, journaled in Redis
  (`enforcement:{edition}:jobs`) before the caller hears back, retried after a 429, carried out
  after a restart. Chats take turns; within one, locks first, then bans and kicks, mutes, deletes,
  unbans, but a person's jobs keep their order. Callers use `Methods.BanUser`/`KickUser`/... as
  before; they wait up to 3s and treat "queued" as done. Telegram's "is an admin" is believed for 10
  minutes and "no rights" makes the chat probed once a minute (jobs wait 10 minutes for the rights).
  At least once: a job Telegram took just before a crash can run again after it; all are idempotent.
- **`CommandGate`/`CommandLimiter`**: commands, buttons and @admin reports per user and per group,
  with the slow lane (`slowlane:{user}` in Redis) instead of a ban. Real enforcement refunds the
  command's cost, so admins banning raiders are never slowed.
- **`RaidGuard`** (`Raids/`): signs per group from untrusted members only (`MemberTrust`: admins,
  the `auth`/`watch` lists and established members never count), raid mode per `RaidMode` (human by
  default, auto, off; `/raid`, `/raidmode`), state in Redis (`raid:{edition}:{chat}`). Only harm
  (the same link, file or long text, floods, bare links) closes a group or touches people in auto
  mode; a surge of joins or the same plain text only holds new joiners back. Buttons carry their
  incident (`raid:{chat}:{action}:{incident}`). A restart forgets the last minute of signs; spam
  posted then that had not yet added up to a sign is not seen again (the chaos run allows for it).
  What admins do and what to check when one goes wrong: `docs/raid-runbook.md`.

- **Caches that keep a raid from costing a call per raider**: `AdminDirectory` (each group's admins,
  60 s, kept current from promotions and demotions in member updates; `chat:{g}:adminses:{u}` only
  means elevated), `ChatDetails` (getChat, 10 min), `MentionResolver` (where an @mention points, one
  lookup queue), `GroupSettings` (a busy group's settings hashes, 10 s), the queue's own record of
  who it muted, and quiet mode (`RaidGuard.Quiet`: no per-member notices or welcome, the log channel
  as a digest) while a raid alert or raid mode is on.

The cross-process seam (`IFloodSignal`) is a no-op; see `docs/postgres-rollout.md`.

## Redis

StackExchange.Redis 2.x. The connection is established lazily in `Redis.Start()` —
**never move it back into a static field initializer**, which is what made the old build
throw `TypeInitializationException` on Linux before anything could be logged.

The instance is shared with blackwolf. Enforcer uses DB index 0 (`Constants.EnforcerDb`).
`Redis.SaveRedis()` issues a BGSAVE and needs `AllowAdmin`.

Message objects stashed in Redis (`messageObject{i}` in `FlagCommands`) were serialised by
the 13.x types. The read path tolerates failures so pre-migration entries degrade rather
than throw — keep that.

### Repositories

We are working towards a Postgres migration. **New** data access goes behind a repository
interface in `Data/` (e.g. `IInlineBotBlockRepository`), with the Redis implementation
next to it and wiring in `Data/Repositories`. Callers never touch `Redis.db` for that
data. Commands are reflection-bound statics, so `Repositories` is a static composition
root rather than constructor injection. The existing direct `Redis.db` calls move over
when that code is touched.

The PostgreSQL migration goes **one domain at a time**, with Redis kept running; progress and
setup are in `docs/postgres-rollout.md`. The approved schema lives on `feature/postgres-migration`
(`docs/storage-erd.md`); copy tables from it unchanged rather than redesigning them. Do not port
that branch wholesale: it was one large migration, which is what the staged rollout replaces.

---

## Deployment

Docker is **artifact transport, not runtime**. CI builds `enforcer-normal` and
`enforcer-premium`, pushes to `registry.kayotech.nl`, and notifies the deploy chat.
blackwolf's DeployBot then extracts the image to disk and restarts the systemd unit.
Nothing runs in a container in production.

Server layout:

```
/opt/enforcer/
├── Languages/                       # shared live XML used by both editions
├── normal/
│   ├── App/                          # Enforcer.dll + bundled Languages/ payload
│   │   ├── Update/                   # staging, picked up by Updater.ApplyPendingUpdate()
│   │   └── Backup/                   # source for /rollbackenforcer
│   ├── TempLanguageFiles/            # edition-specific upload staging
│   └── Logs/                         # tmpfs, 50MB, RAM-backed
└── premium/
    ├── App/                          # Enforcer Premium.dll + bundled Languages/ payload
    │   ├── Update/
    │   └── Backup/
    ├── TempLanguageFiles/            # edition-specific upload staging
    └── Logs/                         # tmpfs, 50MB, RAM-backed
```

At startup each edition seeds XML files missing from the shared live directory from its
bundled payload. Existing shared files are authoritative and are never overwritten by a
restart or deployment. Both processes monitor the shared directory and atomically replace
their in-memory language snapshot when XML files change. Admin upload staging remains
separate, while an accepted upload is atomically published to the shared directory.

Because deployments never update the live XML, the repo's `English.xml` and the live one can
drift apart. They had until October 2026: the live file had keys and text edits that git did
not. Treat the repo file as the source: change text there, then upload that file with
`/uploadlanguage`. Never upload an older repo copy over the live file without comparing them.

### Language uploads (`LanguageUpload/`)

Ported from blackwolf (`Werewolf Control/LanguageUpload/`, 2026-10). `/uploadlanguage` replying to an `.xml`
or a `.zip` of them (Esme sends it in the translators group) posts **one** message; a language admin
(`langAdmins`) or dev taps Review and decides in an **ephemeral** menu only they see (a normal message in
private). The chat message is then edited into the result ("Uploaded 7 of 12"). A plain `/uploadlanguage`
in the translators group is answered by premium only.

| File | What it does |
|---|---|
| `BatchIntake.cs` | Upload → files. Zip: top-level `.xml` only, plain names, count/size limits counted on what unpacks |
| `LanguageValidator.cs` | The checks, also used by /validatelanguages: language node and base length, clashes (fatal); placeholders, a key twice, GIF text over 200 (warnings); missing strings (info) |
| `BatchBuilder.cs` | Checks each file against English, the live files and the other files of the upload |
| `LanguageBatch.cs` | State: **Fatal** never uploads, **Warning** grouped by kind + key (accepting lets it through in every file), **Info** never blocks |
| `BatchStore.cs` | `{TempLanguageFilesPath}/batches/<id>/`, one change at a time per batch, 24h |
| `BatchMenu.cs` / `BatchReport.cs` / `IssueText.cs` | Pure rendering; callback data `lb:<id>:<op>:<n>` |
| `LanguagePublisher.cs` | `.enforcer-languages.lock` beside the live folder (both editions), re-check, temp file + rename |
| `LanguageUploads.cs` | Telegram glue: download, post, callbacks, ephemeral send/edit |

DeployBot commands (run in blackwolf's deploy chat): `/enforcerstatus`,
`/upgradeenforcer`, `/forceenforcer`, `/rollbackenforcer`, `/startenforcer`,
`/stopenforcer`. They share blackwolf's `OpLock`, so werewolf and enforcer deploys cannot
interleave. Bootstrap an empty installation with `/forceenforcer normal` and
`/forceenforcer premium`; `/upgradeenforcer` requires an existing process to apply the
staged files. Binary rollback does not roll back the persistent shared language directory.

Server-side: `enf status`, `enf restart <config|all>`, `enf logs <config> [service|app]`.

### Logging

1. `Console.Error.WriteLine` → journald (`journalctl -u enforcer-normal -f`)
2. `LogHelper.AppendLog` → rotating files under `LogPath`
3. `LogPath` is a **50MB tmpfs** mounted by `setup.sh` — logs are RAM-backed and
   intentionally lost on reboot

Don't add colour codes or `Console.Title` reads; the former is noise in journald and the
latter throws on Unix.

### Error reporting

Every error goes through `ErrorReporter.Report(e, "what was being attempted")`
(`Helpers/ErrorReporter.cs`). It writes the full exception to the log and sends a short
summary to `ErrorChatId`: the error, the chat, user and message text of the update being
handled, and the top stack frames. The update comes from `ErrorReporter.Current`, which the
update consumer sets and which flows into everything that update dispatches.

- **Report from every catch that handles a failure.** Don't add `Console.WriteLine(e)`, bare
  `LogHelper.Error` or empty catches for errors. A catch for an expected outcome (an admin
  typing an unknown @username, "message to delete not found", a 403 from a user who blocked
  the bot) does not report, and says why in a comment.
- It never throws and never blocks. It sends through `Bot.Api` directly, not `Bot.Send`, so a
  failing send cannot loop back into it. It is safe on the per-message path.
- `ErrorThrottle` limits the chat to one report per distinct error per 10 minutes (numbers
  are ignored when comparing, so the same failure in many chats counts once) and 20 reports a
  minute. Anything held back is counted in the next report, and is always in the log.

---

## Testing

```bash
dotnet test Enforcer5.slnx               # Debug (NORMAL)
dotnet test Enforcer5.slnx -c Premium    # PREMIUM code paths
```

PostgreSQL tests are skipped unless `ENFORCER_TEST_POSTGRES` holds a connection string; each test
creates and drops a schema of its own. Run them against a scratch database whenever `Storage/` or
a PostgreSQL repository changes.

### Group-chat harness (`tests/Enforcer5.Tests/Harness/`)

`HarnessChat` simulates a supergroup: messages, photos (`SendPhotoAsync`) and button presses
(`PressAsync` on a bot message's buttons, `PressDataAsync` for raw callback data) go through the
real `UpdateHandler.RouteAsync`, command dispatch and command code, against `FakeTelegram` (an
`ITelegramBotClient` that answers from simulated member state, with Telegram's refusals such as 400
"member not found"). It waits for all dispatched work (`Bot.WaitIdleAsync`) and the history queue,
then a scenario checks replies, Telegram calls, Redis hashes, PostgreSQL rows and `Errors`
(everything `ErrorReporter` saw). `ExpireTempbansAsync` runs the tempban expiry check.

It is built for the PostgreSQL rollout and grows with it: every new PostgreSQL feature gets
scenarios here (category `Harness`). Redis is a loose Moq stand-in (reads return nothing, writes
are dropped) except for hashes (`FakeRedisHashes`, `chat.Hashes`) and strings, sets and sorted sets
(`FakeRedisKeys`, `chat.Keys`), which keep their values with expiry, so warn counters, the tempban
schedule, the enforcement journal and the slow lane work and a scenario seeds group settings there.
They need `ENFORCER_TEST_POSTGRES` and are skipped otherwise, as in the Docker build. A request
`FakeTelegram` does not know throws and shows up in `Errors`: add it to the fake when a scenario
needs it.

**Time is simulated** (`FakeClock` behind `Helpers/Clock`). It stands still while an update is
handled and only moves when the scenario says so (`AdvanceAsync`), firing each timer at its due time
once all dispatched work is idle or waiting on the clock. `SendAsync`/`JoinAsync`/`PressAsync` let
time run until their work is done; `Deliver` hands an update over without waiting, for bursts (follow
with `SettleAsync(flush: false)`). New code that waits must wait on `Clock` (`Clock.Delay`,
`Clock.WaitAsync`), never `Task.Delay` or `Thread.Sleep`, or the harness cannot see it waiting.

**Telegram's limits are enforced** (`FakeTelegram.Limits`): going over answers 429, and
`LimitRefusals` lists those; a scenario should end with none. `StartMessageStorm`,
`StartFullStorm` and `StartStorm` inject 429s; `SetBotCanRestrict` takes the bot's rights away;
`CrashAndRestartAsync` drops everything in memory and reloads from Redis. `Group` adds more chats to
the same bot. `SimulatedBot` is the same without PostgreSQL or routing, for the enforcement queue
tests that run in the Docker build.

**Raids** (category `Raid`): `RaidScenarios`, `RaidTelemetryScenarios` (a recorded raid replays with
`RaidReplay`), and **`ChaosScenarios`**: seeded random raids across several groups with storms, lost
rights and crashes, checking every rule after each. It runs 3 seeds by default. **Before pushing a
change to `Helpers/TelegramGateway`, `FloodControl`, `Enforcement/`, `CommandLimiter` or `Raids/`,
run 500:** `ENFORCER_CHAOS_RUNS=500 enf-sql subprod --run -- dotnet test Enforcer5.slnx --filter
FullyQualifiedName~ChaosScenarios` (about an hour). A failure names its seed; replay it alone with
`ENFORCER_CHAOS_SEED=<seed>`; `ENFORCER_CHAOS_PROGRESS=<file>` logs each seed as it finishes.

The PostgreSQL tests reuse migrated schemas (`TestPostgres.MigratedSchemaAsync`, emptied between
tests) and turn `synchronous_commit` off: subprod takes 250-800 ms to flush a commit, which made a
single raid scenario take minutes.

`tests/Enforcer5.Tests` is MSTest + Moq, matching blackwolf. It reaches the app's internals
through `InternalsVisibleTo`. Without the database the suite runs in about six seconds.

**The Docker build runs the tests** (`Dockerfile`, before `publish`), once per image and so
once per config. A failing test fails the image build, so CI pushes nothing and DeployBot is
not notified. This costs no separate CI job; the CI minute budget is limited, so keep these
tests fast and self-contained: no network, no Redis, no Telegram, no sleeps. Anything slower
belongs in a pre-commit hook, not in the image build.

What is covered, and where to add tests:

- Pure logic: parsing, matching, content-type classification. Test it directly.
- Repositories (`Data/`): test the Redis implementation against a Moq `IDatabaseAsync`.
  `StorageFormat_IsStable` pins the stored layout; changing it needs a data migration.
- Moderation decisions: handlers that read only from `MessageContext` can be tested by
  building one by hand. Paths that reach `Bot.Api` or `Redis.db` cannot be tested yet.
- `LanguageFileTests`: every literal key used in code exists in `English.xml`, there are no
  duplicate keys, and every command has `hcommand` help. Both checks pass with no exceptions;
  the test has an (empty) known-gaps list that may only ever shrink.
- `CommandRegistrationTests`: every `[Command]`/`[Callback]`/`[Query]` binds exactly as
  `Bot.Initialize` does it, and triggers are unique.

Beyond the unit tests, behaviour against Telegram still needs a manual run against a **test
bot token and a scratch Redis DB**.

Never point a second consumer at production Redis — two instances fight over
`bot:last_update` and double-process every update.

---

## Known issues (do not fix without asking)

- Tempbans in flight at migration time run ~2h long, because their stored expiry keys used
  the old `UtcNow+2h` convention. One-time, deliberate.
- ~330 blocking `.Result` calls and static mutable state throughout. Kept to match
  blackwolf and keep the migration reviewable.
- 51 build warnings, nearly all `CS0168` (unused exception variables) from the original
  codebase's empty catch blocks.
