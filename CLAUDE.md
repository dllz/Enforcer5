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
| `src/Enforcer5/Commands/` | Commands, callbacks, inline queries, menus |
| `src/Enforcer5/Attributes/` | `[Command]`, `[Callback]`, `[Query]` — bound by reflection at startup |
| `src/Enforcer5/Languages/` | XML localisation, copied to output |
| `deploy/` | `setup.sh`, `enf.sh`, systemd unit templates |

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
| `RedisConnection` / `RedisPassword` | Shared instance with blackwolf; DB index 0 |
| `PaymentProviderToken` | Telegram Payments. Never commit this |
| `ErrorChatId` | Errors and startup notices; falls back to `Constants.Devs[0]` |
| `LanguagesPath` / `TempLanguageFilesPath` / `LogPath` | Relative paths resolve against the app dir |
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

---

## Deployment

Docker is **artifact transport, not runtime**. CI builds `enforcer-normal` and
`enforcer-premium`, pushes to `registry.kayotech.nl`, and notifies the deploy chat.
blackwolf's DeployBot then extracts the image to disk and restarts the systemd unit.
Nothing runs in a container in production.

Server layout per config:

```
/opt/enforcer/{normal,premium}/
├── App/                    # WorkingDirectory; Enforcer[ Premium].dll + Languages/
│   ├── Update/             # staging dir, picked up by Updater.ApplyPendingUpdate()
│   └── Backup/             # written before each upgrade, source for /rollbackenforcer
├── TempLanguageFiles/      # admin uploads — OUTSIDE App/, survives deploys
└── Logs/                   # tmpfs, 50MB, RAM-backed
```

DeployBot commands (run in blackwolf's deploy chat): `/enforcerstatus`,
`/upgradeenforcer`, `/forceenforcer`, `/rollbackenforcer`, `/startenforcer`,
`/stopenforcer`. They share blackwolf's `OpLock`, so werewolf and enforcer deploys cannot
interleave.

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

There are no unit tests. Verification is: build both configs, `docker build`, then run the
container against a **test bot token and a scratch Redis DB**.

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
