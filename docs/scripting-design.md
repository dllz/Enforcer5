# Scripting and automation: design

Draft for Daniel's review (2026-10-31); his answers of 2026-10-31 are worked in (section 11).
Nothing is built yet. The action seam this rests on has its
own document, `docs/action-seam.md`; read it before changing anything under "Rules" or "Batch".

## 1. Where this is going

Enforcer becomes a bot a group can customise and automate itself, not only configure. Group
admins describe what they want, their own LLM writes the script from a skill we publish, and the
bot runs it safely.

The full scope Daniel wants to support over time:

| Area | Example |
|---|---|
| Rules on actions (before / after) | block removing a warn until 24 hours after it was given; after a ban, post a note in the admin chat |
| Batch | `/batch ban` on a list of ids; any action command over a list |
| Scheduling | `/schedule` a warn removal; check every day whether someone has played enough games |
| Joins | on join, check their Werewolf games or whether they were in the group before, then let them in, mute them or send a captcha |
| Other bots | send a command to another bot, pick up its answer, run a script on it |
| Werewolf data | a `blackwolf` module in the scripting API (player stats from blackwolf_api) |

**Version 1 is only the first two rows: rules on actions, and `/batch`.** Everything else is
listed so v1's shapes do not have to be broken to add it (section 9).

## 2. Principles

1. **Enforcement never depends on a script.** A script that fails, times out or is over budget
   lets the action go ahead (fail open), and the admins hear about it. Raid mode, flood control,
   global bans and the enforcement queue never wait for a script and cannot be blocked by one
   (`docs/action-seam.md`, section 5). This is the same rule Detection follows.
2. **Scripts propose, the bot acts.** A script never calls Telegram, Redis or PostgreSQL. It
   reads what it is given and returns *intents* (block, change, do this too). The bot carries
   intents out through the action seam, with the permissions of the admin who wrote the script,
   in that script's group only, within the normal rate limits.
3. **A group's scripts only see that group.** The input holds the group's own data about the
   members in the action, nothing about other groups.
4. **Both editions**, one shared PostgreSQL schema, like Detection.
5. **Written for an LLM author.** The language is one LLMs write well, the API is typed in a
   `.d.ts` file, and every error is a sentence an LLM can fix the script from.
6. **New data in PostgreSQL** (scripts, versions, runs, batches); Redis only as a cache.

## 3. Language: JavaScript on Jint

[Jint](https://github.com/sebastienros/jint) (BSD-2, about 37M NuGet downloads, maintained, used
by Orchard Core) runs JavaScript inside the process with no access to .NET. Chosen over the
alternatives (research in the `enforcer` memory dataset, 2026-10-31) because:

- LLMs write JavaScript reliably; a language of our own or an expression language like NCalc
  would cost every author a round of syntax errors.
- Jint 4.x has a profile for untrusted code, `Options.ForUntrustedCode(UntrustedCodeLimits)`:
  no CLR or reflection access, no module loading, no `eval`/`Function`, limits on time,
  statements, memory, recursion, regular expressions and result size.
- One language for rules, batch and later schedules: one sandbox, one skill.

Rejected: Lua (MoonSharp, Lua-CSharp; the sandbox is ours to build, and a script can catch the
limit error with `pcall`), Microsoft RulesEngine, Dynamic LINQ, DynamicExpresso (expressions over
.NET types), NLua, NeoLua, ClearScript, Roslyn scripting (native code or full .NET access),
NRules (rules in C#), CEL (right idea, but the .NET ports are one-person projects).

Cost: about 3 MB per edition (Jint 2.6 MB, Acornima 0.4 MB) on a 41 MB build.

### Limits (per run)

| Limit | Value | Why |
|---|---|---|
| Wall time | 50 ms before, 200 ms after | a before rule sits on an admin's command |
| Statements | 20,000 | catches loops long before the time limit |
| Memory | 4 MB | Jint's own accounting |
| Recursion | 32 | |
| Regex | Jint's untrusted default, plus .NET's match timeout | |
| Intents returned | 5 | one block or change, plus up to 4 extra actions |
| Output text | 1,000 characters per message | |

Per group: 20 rules, 2,000 rule runs an hour; over budget, rules are skipped (fail open) and the
admins are told once. Agreed as starting values (Daniel, 2026-10-31); measured in the harness
before release.

Jint's limits are checked between statements, so they cannot interrupt our own host functions.
Host functions therefore never do I/O: everything a script can read is loaded before it runs
(`docs/action-seam.md`, section 7).

Scripts are parsed once (`Engine.PrepareScript`) and cached per version; an engine per run is
cheap. Jint is reviewed on every upgrade (the untrusted profile is the security boundary).

## 4. Rules

A rule is one script attached to actions. It says when it runs and what it does:

```js
rule({
  name: "Warns stay for 24 hours",
  when: "before",                       // or "after"
  actions: ["removewarn", "resetwarns"],
  sources: ["command", "button"],       // optional; default: everything an admin does
}, (action, ctx) => {
  const last = ctx.target.warns.lastAt;  // Date or null
  if (last && ctx.hoursSince(last) < 24)
    return block(`Warns can be removed 24 hours after they were given (in ${Math.ceil(24 - ctx.hoursSince(last))} h).`);
});
```

- `action` is the request: kind, source, actor, target, reason, duration, the message it is
  about, and for an escalation its parent (`docs/action-seam.md`, section 4).
- `ctx` is read-only data loaded before the run: the target's warns, prewarns, last warn time,
  warn history (last 20), join date, trust level, admin status; the actor's name and rights; the
  group's warn limit and settings; `now`, plus small helpers (`hoursSince`, `daysSince`).
- **Before** rules return nothing (allow), `block(message)`, or `change({ reason, duration,
  silent })`. They can add extra actions with `also(...)`. First block wins; changes apply in
  rule order.
- **After** rules see the result too (`action.result`: done, queued, refused) and can only add
  actions (`also`) and messages (`say`, to the group or the admin chat).
- Actions a script adds go through the seam as source `script`. They run built-in checks but no
  script rules, so rules cannot trigger each other (depth 1).

### Who and how

- **Group admins** (Daniel, 2026-10-31). Extra actions a rule adds (`also`) run with its
  author's rights, checked when the rule runs, so an admin who cannot ban cannot ban through a
  rule either. If the author is no longer an admin, their rules pause and the other admins are
  told.
- `/rule add` replying to a `.js` file or a message with the code: the bot checks it (parse,
  `rule(...)` call, known actions and sources, a dry run against made-up input), then shows what
  it understood ("Before removewarn and resetwarns, from commands and buttons") with Enable /
  Cancel. Errors come back as plain sentences with the line.
- `/rule test <name>` runs it against a member without acting ("would block: ...").
- `/rule list`, `/rule show <name>`, `/rule off|on <name>`, `/rule delete <name>`,
  `/rule log <name>` (last runs: allowed, blocked, changed, failed, time).
- Every saved change is a new version; `/rule show` can show the previous one.

### The skill

`/scripthelp` sends two files the admin gives their LLM:

- `enforcer-scripting.md`: what rules are, the rule shape, every action and source, what
  `block`/`change`/`also`/`say` do, the limits, and worked examples (warn removal after 24 h,
  no bans without a reason, mute instead of a third warn for new members, a note to the admin
  chat after a ban).
- `enforcer.d.ts`: the exact types of `action`, `ctx` and the functions.

The API has a version (`api: 1` in the files and in each saved script) so later changes do not
break saved rules.

## 5. Batch

`/batch <command> [arguments]` replying to a list of ids (a message, or a `.txt`/`.csv` file)
runs that command once per id, as the admin who sent it. Details, including which commands
take part, in `docs/action-seam.md`, section 9. In short:

- The bot reads the ids, drops duplicates, admins and the bot itself, and shows a preview:
  "Ban 312 members: 4 are already banned, 2 are admins (skipped). Reason: spam ring". Confirm or
  Cancel.
- On Confirm, a batch is saved in PostgreSQL and run in the background through the seam (source
  `batch`), so it is paced by the enforcement queue, continues after a restart, and before rules
  apply to every item.
- One progress message, edited as it goes, then a summary (done, refused, blocked by a rule, with
  the reasons). One log channel entry for the batch, not one per member.
- Limits: one running batch per group, 5,000 ids per batch.

## 6. Storage (PostgreSQL)

| Table | Holds |
|---|---|
| `scripts` | id, group, name, kind (`rule`), when, actions, sources, enabled, current version, author, created, updated |
| `script_versions` | script, version, source text, api version, author, created |
| `script_runs` | script, version, action id, outcome (allowed, blocked, changed, failed, skipped), error, ms, at. Kept 30 days |
| `batches` | id, group, admin, command, arguments, state, counts, created, finished |
| `batch_items` | batch, target, state, outcome, reason |

Each group's enabled rules are cached in memory (with their prepared scripts) and reloaded on
change; the other edition sees a change within a minute (as `AdminChats` does). With PostgreSQL
down, cached rules keep running and nothing new can be saved.

## 7. Strings

All new texts (`/rule`, `/batch`, `/scripthelp`, errors) go into `English.xml` and are uploaded,
with translations for the files we maintain, before the push (the strings rule). Texts written by
scripts (`block("...")`, `say("...")`) are the admins' own and are not translated.

## 8. Security summary

| Threat | Answer |
|---|---|
| Script reaches .NET, files, network | Jint untrusted profile: no CLR, no modules, no eval; host exposes plain data and pure helpers only |
| Endless loop, memory bomb, ReDoS | statement, time, memory, recursion and regex limits; fail open |
| Script acts in another group | intents are bound to the script's group; ids from a script are checked against it |
| Script does more than its author could | intents run with the author's rights, checked at run time |
| Rules trigger rules | actions from scripts run no script rules (depth 1) |
| Script blocks raid or flood defence | those sources cannot be blocked (`docs/action-seam.md`, section 5) |
| A runaway rule floods the group | message lanes and per-group budget; `say` counts against them |
| Data leaking into the script | input is that group's data about the members of the action |
| Personal data in logs | `script_runs` stores outcome and error, not the input |

## 9. Later (not in v1, but the shapes allow it)

- **Schedules.** `schedule(when, handler, data)` from a rule, and `/schedule`. Durable jobs in
  PostgreSQL. A handler runs as a fresh event; nothing waits in memory. A scheduled action is an
  action request with source `schedule`.
- **Joins.** An `onJoin` event (and join requests: Enforcer does not handle `chat_join_request`
  today) with `ctx.joins` from `group_joins`, answering approve, decline, mute or captcha.
  Captcha is a new feature of its own.
- **Werewolf data.** A `blackwolf` module, loaded *before* the run like the rest of `ctx` (no I/O
  inside scripts): `/player/{id}/stats` gives global games played, won and rank. Games per group
  for a player would need a new blackwolf_api endpoint and Enforcer needs its own API key.
- **Other bots.** Send a command to an allowed bot, wait for its answer as an event, run a
  handler on it. Only bots a group allows explicitly; the trusted-bot filter
  (`Helpers/BotSenderFilter.cs`) stays the gate.

Each of these is a new *event* feeding the same engine, intents and seam. Waiting (captcha
answers, another bot's reply, a schedule) is always a stored continuation, never a script
sitting in memory.

## 10. Build order

Each step is a local commit; nothing is pushed until Daniel says "push".

1. Action seam, steps A to F of `docs/action-seam.md` (no visible change except the bugs it fixes).
2. Script host: Jint with the limits, `ctx` loading, intents, tests for every limit.
3. Rules: tables and migration, `/rule` commands, hooks wired to the seam, the skill files.
4. `/batch`.
5. Release check: the harness scenarios, 500 chaos seeds (the seam touches enforcement), both
   editions, strings uploaded.

## 11. Decisions and open questions

Decided (Daniel, 2026-10-31):

- Rules are written by the group's admins (section 4).
- The limits in sections 3 and 5 are the starting values.
- Detection, filter and length actions get no before hooks in v1 (`docs/action-seam.md`, section 5).
- A blocked or refused max-warns ban keeps the warns and tells the admins
  (`docs/action-seam.md`, section 8).
- Batch and script actions get their own label sources (`docs/action-seam.md`, section 13).

Open:

1. Should after rules also see actions taken in the Telegram app (proposed: yes, source `app`)?
2. Premium-only extras (more rules, bigger batches), or the same for both editions?
