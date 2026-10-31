# The action seam: design

Draft for Daniel's review (2026-10-31); his answers of 2026-10-31 are worked in (section 14).
Part of `docs/scripting-design.md`, but useful without
scripts: it gives every moderation action one path, which fixes several bugs and makes `/batch`,
scripts and later schedules possible.

Line references are to `Migration` at `9d1d32a`. Paths are under `src/Enforcer5/`.

## 1. What there is today

**The bottom layer is already unified.** Every ban, kick, mute, unban, unmute, sender-chat
ban, chat permission change and moderation delete is an `EnforcementJob` in
`EnforcementQueue`; only `Enforcement/EnforcementExecutor.cs:87-139` calls Telegram for them
(0 direct calls elsewhere). The queue paces, journals, retries and refuses known admins
(`EnforcementQueue.cs:136-153`), and `EnforcementExecutor.Record` (`:194-238`) writes ban and
mute history, which writes `Decisions` labels (`Helpers/BanHistory.cs:84`).

**Everything above the queue is per caller.** That is where the seam goes:

| Problem | Where |
|---|---|
| About 40 calls to `Methods.BanUser/KickUser/TempBanUser/UnbanUser/MuteUser/TempMuteUser/UnmuteUser`, 9 to `Commands.Tempban`, 10 to `Commands.Warn`, about 22 straight to `EnforcementQueue.Run/SubmitAsync` (12 in `Raids/RaidGuard.cs`) | all over `Commands/`, `Detection/`, `Raids/` |
| Warns never touch the queue: Redis `HINCRBY` in `Commands.Warn` (`Commands/BanCommands.cs:143-294`), a second copy in `userbuttonwarnuser` (`Commands/GroupCommands/AdminCommands.cs:1034-1079`) without the prewarn conversion, `totalWarns` or the report, media strikes in `OnMessage.cs:355-370`, removal in `WarnRemovals.ApplyAsync` (`WarnRemovalCommands.cs:258`) | |
| Side effects are copied into each caller: tempban schedule cleanup, `SaveBan`, `AddBanList`, `{chat}:userJoin`, the log channel line | `/ban` (`BanCommands.cs:296-420`), `banflag` (`FlagCommands.cs:502`), `userbuttonbanuser` (`AdminCommands.cs:997`) |
| The copies have drifted: the two ban buttons never cancel a running tempban (exact match on a value with more fields, and normal-edition keys in premium), so the member is unbanned when it expires | issue #24 |
| Permission checks are in the dispatcher only (`Handlers/UpdateHandler.cs:303-366`, callbacks `:876-946`); the command methods trust their caller | |
| The source of an action (command, button, report, auto, raid, app) is guessed from `ErrorReporter.Current` inside `Decisions.Record` (`Detection/Decisions.cs:74-84`) | |
| `/d` deletes without a decision; `/removewarn` and `/resetwarns` only take a reply, not an id | `MemberCommands.cs:123`, `WarnRemovalCommands.cs:333-336` |
| Telegram-app actions are seen afterwards in `MemberEvents.FromChatMember` → `Decisions.FromChatMember` (`Decisions.cs:132-175`) and only recorded | |

## 2. Goals and non-goals

Goals:

1. **One entry point** for every action on a member, whoever asks for it: `Actions.Run(request)`.
2. **The action owns its effects**: state (Redis lists and counters, tempban schedule), history,
   decisions and the log channel line. Callers only render the answer (reply, popup, notice).
3. **Hooks**: before (may block or change) and after (may react), for scripts and later
   built-in features.
4. **Callable without an Update**, with any target id: what `/batch`, scripts and schedules need.
5. **No change in behaviour** for admins while the seam goes in, apart from the bugs it fixes.

Non-goals:

- Replacing `EnforcementQueue`. It stays the transport to Telegram, unchanged.
- Putting anything in front of raid, flood or global-ban enforcement that could stop or slow it.
- Routine deletes (flood, locks, channel posts, housekeeping) stay direct queue jobs; only the
  deletes an admin asks for are actions.
- Settings changes, auth/elevate lists and trust overrides: later, as their own kinds.

## 3. Layers

```
 caller (command, button, report, Detection, filter, flood, raid, batch, script, app observer)
   │  ActionRequest
   ▼
 Actions.Run ── 1 normalise ── 2 authorise ── 3 guards ── 4 before hooks ── 5 execute ── 6 record ── 7 after hooks
                                                                            │
                                                                            ▼
                                                                 EnforcementQueue (unchanged)
   │  ActionResult
   ▼
 caller renders it (reply, popup, notice) – or nothing
```

## 4. Types

```csharp
namespace Enforcer5.Actions;

internal enum ActionKind
{
    Ban, TempBan, Unban, Kick, Mute, TempMute, Unmute,
    Warn, Prewarn, RemoveWarn, ResetWarns,      // RemoveWarn/ResetWarns carry the WarnKind
    Delete,                                     // admin deletes only (/delmsg, /d, delflag)
    BanChannel, UnbanChannel,                   // a member posting as a channel
}

internal enum ActionSource
{
    Command, Button, Report,                    // an admin, in the bot
    Batch, Script,                              // an admin, indirectly
    Escalation,                                 // a warn reaching the limit
    Detection, Filter, Flood, Limits,           // the bot's own checks (Limits: length and media)
    Raid, GlobalBan, MuteOnJoin,                // the bot's defences
    Recovery, Expiry,                           // data recovery, a tempban running out
    App,                                        // seen afterwards: an admin in the Telegram app
}

internal sealed record ActionRequest
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required ActionKind Kind { get; init; }
    public required long ChatId { get; init; }
    public required long TargetId { get; init; }
    public long? ActorId { get; init; }            // null: the bot itself
    public required ActionSource Source { get; init; }
    public string Reason { get; init; }
    public TimeSpan? Duration { get; init; }       // TempBan, TempMute
    public WarnKind WarnKind { get; init; }        // Warn kinds; RemoveWarn/ResetWarns
    public bool AlsoMediaWarns { get; init; }      // ResetWarns from the warn buttons
    public bool RevokeMessages { get; init; }
    public bool Silent { get; init; }              // no group notice (raid quiet mode, a script's change)
    public int? AboutMessageId { get; init; }      // the post it is about (Decisions, banned media)
    public Message About { get; init; }            // when the caller has it
    public WarningRef Trigger { get; init; }       // the warn that reached the limit
    public ReportRef Report { get; init; }
    public Guid? ParentId { get; init; }           // the warn an escalation came from; the batch item
    public long? BatchId { get; init; }
    public long? ScriptId { get; init; }
    public int Depth { get; init; }                // 0 from a person or the bot; 1 from a script
    public bool Record { get; init; } = true;      // false: /kickme
}

internal enum ActionOutcome
{
    Done,          // Telegram did it, or warns changed
    Queued,        // will happen (rate limited, waiting for rights); callers treat it as done
    Blocked,       // a before hook or a guard said no; Message says why
    Refused,       // Telegram refused for good; Refusal says why
    NothingToDo,   // no warns to remove, not banned, ...
    Failed,        // our error; reported through ErrorReporter
}

internal sealed record ActionResult(ActionOutcome Outcome, ActionRequest Applied)
{
    public Refusal Refusal { get; init; }
    public string Message { get; init; }       // Blocked: the rule's or guard's text
    public string BlockedBy { get; init; }     // rule name, or a guard's name
    public long? WarnsNow { get; init; }       // warn kinds: the count after
    public ActionResult Escalation { get; init; } // a warn that reached the limit
    public bool Succeeded => Outcome is ActionOutcome.Done or ActionOutcome.Queued;
}
```

`Applied` is the request after before hooks changed it, so the caller announces what really
happened (the duration a rule shortened, the reason it added).

Entry points, in the codebase's style (static, like `EnforcementQueue`):

```csharp
internal static class Actions
{
    internal static Task<ActionResult> RunAsync(ActionRequest request);
    internal static ActionResult Run(ActionRequest request);   // waits as EnforcementQueue.Run does (up to 3 s, then Queued)
}
```

## 5. Who can be blocked

Before hooks only run for actions an admin chose. The bot's defences never wait for a script.

| Source | Before hooks | After hooks | Why |
|---|---|---|---|
| Command, Button, Report, Batch | yes | yes | admin decisions: what business rules are for |
| Script | no (depth 1) | no | rules cannot trigger rules |
| Escalation | yes | yes | its own action: a rule may turn "ban at 3 warns" into a mute |
| Detection, Filter, Limits | no | yes | automatic checks, configured in Detection already; may change later |
| Flood, Raid, GlobalBan, MuteOnJoin | no | no | defences; a raid makes hundreds of these and quiet mode is on |
| Recovery, Expiry | no | yes | |
| App | – | yes | already happened; an after rule may react (e.g. undo it) |

`Detection`/`Filter`/`Limits` before hooks are left out of v1 (Daniel, 2026-10-31): a slow rule
on the message path is the one cost the scripting design rules out. Revisit with data.

## 6. The pipeline

1. **Normalise.** Fill `ActorId` for a raid button (as `Decisions.cs:78` does today), clamp
   durations to Telegram's range, turn a negative target id into `BanChannel`.
2. **Authorise.** For sources with an actor: the actor may do this kind of action in this chat
   (group admin, global admin or dev, as the dispatcher checks today). For `Batch` this is the
   first and only check, so it must be here and not in the dispatcher. `Script` actions run as an
   admin of the script's group and are only checked to be in that group. Each admin's own
   Telegram rights (ban users, delete messages) are not checked, as today (Daniel, 2026-10-31).
   Failure: `Blocked` with `BlockedBy = "rights"`.
3. **Guards** (built in, always run, no scripts): target is not the bot, not the actor
   themselves (except `/kickme`), not a known admin (`AdminDirectory`; the queue still checks
   too), not an anonymous admin or the group itself. For warn removals: there is something to
   remove (`NothingToDo`). Same answers as today.
4. **Before hooks** (section 5 decides whether they run; never on the creator's or devs'
   actions, so a rule cannot lock the owner out). Each enabled hook for this kind and
   source, in order. A hook returns allow, block(message) or change(fields). First block stops;
   changes apply in order to `Reason`, `Duration` and `Silent` only. A different action is block
   plus `also(...)`: the kind of an action never changes in place. A hook that throws, times out
   or is over budget counts as allow, and is recorded.
5. **Execute.** One handler per kind (section 8): the state change and the queue job.
6. **Record.** Everything the action owns (section 8): history, decisions, the log channel line,
   counters. Moved out of the callers.
7. **After hooks.** Queued to run after the result is returned; the caller never waits for them.
   Extra actions from hooks (`also`) are new requests with `Source = Script`, `Depth = 1`,
   `ParentId` = this action.

The caller gets the `ActionResult` after step 6, as quickly as today: steps 1 to 3 are in memory,
step 4 costs nothing when the group has no rules (one dictionary lookup), and step 5 waits on the
queue exactly as the `Methods.*` helpers do now (`CallerWait`, 3 s).

`CommandGate` refunds stay as they are: they happen inside `EnforcementQueue.Run`.

## 7. What a hook can read

Loaded once in step 4, only when a hook will run, before any script starts (Jint cannot interrupt
our own functions, so scripts never do I/O):

- the request (`action`);
- target: warns, prewarns and media warns (Redis), last warn time and the last 20 warn history
  entries (PostgreSQL `warning_events`), joined at, established or new (`MemberTrust`), admin or not;
- actor: id, name, admin rights;
- group: warn limit and action, language, whether raid mode is on.

PostgreSQL reads have a 200 ms timeout; on timeout the hook gets `null` for those fields and the
skill tells authors to handle `null`.

## 8. What each kind owns

Today's caller code, moved into the action. The caller keeps only its answer.

| Kind | Execute | Record (owned by the action now) | Stays with the caller |
|---|---|---|---|
| Ban | queue `Ban` (revoke) | cancel any tempban (schedule, set, both editions: fixes #24); `SaveBan`; `AddBanList`; delete `{chat}:userJoin`; `BanHistory`/`Decisions` (executor, as today); log channel line | reply, forward of the replied post (`/ban`), popup |
| TempBan | queue `Ban` until; schedule entry (`Commands.Tempban`, `BanCommands.cs:493-555`) | `SaveBan`; history; log line | notice |
| Unban | queue `Unban` | remove schedule entry, `bannedlist`; history | reply |
| Kick | queue `Kick` | history (unless `/kickme`); log line | reply |
| Mute, TempMute | queue `Mute` | `Repositories.Mutes`, history (executor) | reply |
| Unmute | queue `Unmute` | mute lists, history | reply |
| Warn, Prewarn | warn counters (`IWarnRepository`), prewarn conversion, `totalWarns` | `WarnHistory`, `Decisions`; then **escalation** (below) | the warn message and its buttons |
| RemoveWarn, ResetWarns | `WarnRemovals.ApplyAsync` | `WarnHistory`, reason, log line | the reason prompt (`WarnRemovals.Request`), which runs *before* the action is requested |
| Delete | queue `Delete` | `Decisions.Deleted` (now also for `/d`) | |
| BanChannel, UnbanChannel | queue `BanSenderChat`/`UnbanSenderChat` | log line | reply |

**Escalation.** A warn that reaches the group's limit asks for a second action (`Ban` or
`Kick`, `Source = Escalation`, `Trigger` = the warn, `ParentId` = the warn's id, reason "Max
warns", no actor as today, so its label stays `auto`; the warn's admin is reachable through the
parent). It goes through the whole pipeline, so a before rule can see `action.parent` and block or
replace it. If it is blocked or refused the warns are *kept* and the admins are told (Daniel,
2026-10-31): the warn message says the limit was reached and why the ban or kick did not happen
(the rule's name and text, or Telegram's refusal), and the log channel gets the same line. Today
the warns are set to 0 with "Max warns" whether or not the ban went through
(`BanCommands.cs:255-256`).

**One warn implementation.** `Commands.Warn` and `userbuttonwarnuser` become one handler; the
button gets the prewarn conversion, `totalWarns` and its report like the command. Its visible
text stays the same.

**Log channel.** The action writes one line in today's wording (`Service.LogCommand` for admin
sources, `Service.LogBotAction` for the bot's own, digested during a raid). Callers stop logging
those actions themselves; `LogCommand` stays for commands that are not actions. A batch writes one
summary line instead of one per member.

## 9. Batch on the seam

`/batch <command> [arguments]` replying to a list of ids (or a `.txt`/`.csv` file).

- **Commands that take part** are the action commands: `ban`, `tempban <duration>`, `unban`,
  `kick`, `mute`, `tempmute <duration>`, `unmute`, `warn`, `prewarn`, `removewarn`, `resetwarns`.
  The command map lives next to `ActionKind` (trigger → kind + how to read the arguments), so a
  command that becomes an action later is batchable by adding one line. "Every command" is reached
  by moving commands into the seam, not by replaying arbitrary commands with fake Updates (which
  skips the dispatcher's checks, logs the wrong target and runs the rate limiter per item).
- **Arguments** are parsed once, the same way the command parses them, with no target in them
  (`/batch tempban 2d spam ring`).
- **Preview** (in memory, no Telegram calls per id): count, duplicates, the bot, admins
  (`AdminDirectory`), already banned (`bannedlist`), nothing to remove. Confirm / Cancel, for
  any admin of the group (Daniel, 2026-10-31; the one who confirms is recorded with the batch),
  valid 10 minutes.
- **Run:** a `batches` row and its `batch_items` in PostgreSQL; a background worker takes one
  item at a time per batch and runs it through `Actions.RunAsync` with `Source = Batch`,
  `BatchId`, the admin as actor, `Silent = true`. Pacing is the queue's (bans 25 a second
  bot-wide, chats taking turns), so a batch cannot starve another group's enforcement. After a
  restart unfinished items continue.
- **Feedback:** one progress message edited at most every 5 seconds; at the end a summary: done,
  refused (with reasons), blocked by a rule (rule names), skipped. One log channel line.
- **Cancel:** a Stop button on the progress message; items not started are dropped.
- **Limits:** 5,000 ids, one running batch per group, the admin must still be an admin when each
  item runs (step 2).

## 10. The Telegram app

`Decisions.FromChatMember` keeps recording as today and, in addition, reports the change to the
seam as an observed action (`Source = App`, the admin as actor, step 7 only). Nothing is
executed: it already happened. It also starts doing the state an app ban skips today (remove from
the tempban schedule on a permanent ban), which is the same code as the Ban kind's record step.

## 11. Steps

Each step builds, passes all tests (both editions) and the 500 chaos seeds where enforcement is
touched, and is one local commit. A, B and C are worth having even if scripting stopped there.

| Step | What | Visible change |
|---|---|---|
| A | `Actions/`: types, `Actions.Run`, no hooks. `Methods.*`, `Commands.Tempban` become thin wrappers building a request; source passed explicitly at the automatic call sites (Detection, filters, flood, limits) and derived for commands and buttons (moving the logic out of `Decisions.cs:74-84`) | none |
| B | Ban side effects into the Ban, TempBan, Unban kinds; callers drop their copies | fixes #24 |
| C | Warns into the seam: one warn handler, escalation as its own action, `RemoveWarn`/`ResetWarns` (and they accept an id), media strikes | the warn button behaves like `/warn` |
| D | Raid, global ban and Mute On Join through the seam (never blockable), deletes asked for by admins (`/d` records a decision) | none |
| E | Log channel lines owned by the actions | wording unchanged |
| F | Hook points: before and after, with in-process C# hooks only; source table of section 5; budgets; a guard test (below) | none |
| G | `/batch` | new command |

Then the script host and rules (`docs/scripting-design.md`, section 10).

## 12. Tests

- **Guard test** (like `CommandRegistrationTests`): no file outside `Actions/` and
  `Enforcement/` submits `Ban`, `Kick`, `Mute`, `Unban`, `Unmute` or `BanSenderChat` jobs, and
  nothing outside the warn handler writes `chat:{c}:warns`. Lists the allowed exceptions
  (routine deletes, raid lock).
- **Harness scenarios** per kind and source: the reply, the Redis and PostgreSQL state, the
  decision label (source must match today's for every existing path), the log line.
- **Equivalence**: before step A, record what each existing scenario produces (replies, Telegram
  calls, labels); every step must produce the same, except where a fix is intended.
- Issue #24 and the warn button get scenarios of their own.
- Hooks (step F): a hook that blocks, changes, throws, loops and overruns; a raid while a group
  has a slow before hook (the raid must not notice); escalation blocked.
- Batch: preview counts, confirm by another admin accepted and by a non-admin refused, restart halfway, Stop, 429 storm
  during a batch, a rule blocking some items.

## 13. Risks

- **Moving side effects** (step B, C, E) is where behaviour can drift. Equivalence tests first.
- **Escalation as its own action** changes when warns reset (only after the ban went through).
  Intended, but visible: "3/3 warns, ban blocked by rule X" keeps the 3 warns.
- **Source mapping**: Detection learns from `member_labels.source`. Labels must keep today's
  values (`command`, `button`, `report`, `raid`, `auto`, `app`); new sources map onto them.
  `batch` and `script` get their own values (Daniel, 2026-10-31): a migration widens
  `member_labels_source_check` (`Storage/EnforcerDbContext.cs:46`), and Detection's readers of
  `source` (`TrustLearning`, the nightly labels, `tools/tuning/export.sql`) are checked for how
  they should count them.
- **Premium `#if` copies** of tempban keys collapse into `RedisTempbanRepository`; both configs
  must be built and tested at every step.

## 14. Decisions and open questions

Decided (Daniel, 2026-10-31):

- No before hooks for Detection, filter and length actions in v1 (section 5).
- A blocked or refused escalation keeps the warns and tells the admins (section 8).
- `batch` and `script` are new label sources (section 13).
- Admins' own Telegram rights are not checked by the seam (section 6, step 2).
- Before hooks never run on the creator's or devs' actions (section 6, step 4).
- Any admin may confirm or cancel a batch (section 9).

Nothing open.
