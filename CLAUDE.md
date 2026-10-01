# Enforcer — Telegram group management bot

Single .NET 10 console app. Redis is the only datastore. Deployed on Linux under systemd,
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
| `src/Enforcer5/Handlers/UpdateHandler.cs` | Update routing, spam detection, command dispatch |
| `src/Enforcer5/Data/` | Repository interfaces, their Redis implementations, and `Repositories` (the composition root) |
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

---

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

---

## Testing

```bash
dotnet test Enforcer5.slnx               # Debug (NORMAL)
dotnet test Enforcer5.slnx -c Premium    # PREMIUM code paths
```

`tests/Enforcer5.Tests` is MSTest + Moq, matching blackwolf. It reaches the app's internals
through `InternalsVisibleTo`. The whole suite runs in about a second.

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
