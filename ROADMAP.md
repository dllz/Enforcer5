# Enforcer — 2026 Modernisation Roadmap

Product-owner review conducted 2026-08-04 against .NET 10 / Telegram.Bot 22.7.0, Redis-only
datastore.

## Assessment

Feature breadth is large and is **not** the problem: 98 `[Command]`s, 70 `[Callback]`s, 2
`[Query]`s, plus flood/media/length/script filters, warns, prewarns, a two-tier elevation/auth
system, report fan-out, and per-group XML localisation. That's a real product.

The problem is that the design assumes Telegram circa 2016. It subscribes to five update types
(`Message`, `CallbackQuery`, `InlineQuery`, `PreCheckoutQuery`, `ShippingQuery` —
`Utilities.cs:248-261`) and two of those are dead code. Everything Telegram added afterwards —
topics, join requests, chat-member events, `sender_chat`, reactions, edits, albums — is invisible
to it. And the threat model it defends against (Arabic character classes, RTL overrides, message
length) is not what 2026 spam looks like.

So this is less "add features" and more "the bot is blind to half of modern Telegram; fix that,
then compete on what matters".

---

## Tier 0 — Correctness blockers

Not features so much as "the bot is visibly broken in a modern group". Ship nothing else first.

### 1. Forum topic support

Zero occurrences of `MessageThreadId` in the codebase. In any topic-enabled group — most large
groups now — every warn, welcome and `/rules` reply lands in *General* instead of the user's
topic. It looks broken.

Mechanical fix (thread the ID through every send helper in `Utilities.cs`) but it touches
everything, so it goes first.

### 2. `MyChatMember` subscription

Bot *addition* is already detected — `Service.BotAdded` fires off the bot appearing in
`NewChatMembers`. Removal is never detected, and rights changes are invisible until something
throws. That is the real gap.

### 3. `sender_chat` / channel impersonation

Nothing reads `SenderChat`; `banChatSenderChat` is unused. Channel impersonation is a top modern
spam vector.

Anonymous admins *are* handled, crudely — see findings below. Related: `IsAutomaticForward` is
unread, so linked-channel posts auto-forwarded into a discussion group get flood-counted and the
channel can be banned.

### 4. Media groups (albums)

Each album item arrives as a separate update. A 10-image album costs 10 flood counts and up to 10
media warns for one user action. Needs debouncing by `MediaGroupId`.

### 5. Edited messages

Not subscribed. Post clean, edit to spam — the oldest evasion there is, and there's no answer.

### 6. Data model hygiene

Gating work; merges with item 23. Detail under [Verified code findings](#verified-code-findings).

---

## Tier 1 — Core modern moderation

### 7. Real join gating

Today `MuteOnJoin` mutes everyone forever with no automatic release, and an admin runs
`/unmutenewjoiners` by hand. That's not a captcha, it's a chore. Need:

- Button captcha with timeout and auto-kick on failure
- Math/emoji captcha as a step up
- `ChatJoinRequest` handling, with optional rules-acceptance before approval
- Invite-link attribution, so a leaking link can be identified and killed

### 8. New-user restrictions

No links, media or forwards for the first N messages or X hours. Cheapest, highest-value
anti-spam available — catches most drive-by spam with zero keyword rules.

### 9. Word/regex blacklist

There is currently **no** content filtering beyond media type and script class. Table stakes;
every competitor has it.

### 10. Link policy that isn't binary

`media/link` is all-or-nothing today. Need domain allow/blocklist, `t.me` invite-link detection
separate from ordinary links, `@username` mention filtering, forwarded-from-channel filtering.

### 11. Purge and clean-on-ban

`/purge` using bulk `DeleteMessages` (100 at a time), `banChatMember(revokeMessages: true)` so
banning actually removes the spam, and auto-delete of join/leave service messages.

### 12. Retire the script filters

`ArabDetection` matches a hardcoded Persian/Arabic character class against every message with one
`Regex.IsMatch` **per character** in a loop. Slow, a blunt ethnicity filter that risks delisting,
and it doesn't catch modern spam. Replace with a language/script policy setting if anyone wants
one.

**Keep RTL-override detection** — that one is a genuine attack.

### 13. Warn decay

Warns are permanent today. Deliberately moved to post-Postgres: `chat:{id}:warns` is
`userId -> int`, and you cannot expire an individual warn from a counter.

### 14. Fix the report pipeline

`/admin` fans out to every admin's PM, but bots can't message users who haven't started them, so
the fallback path does heavy lifting. Modern pattern is a dedicated admin topic or a linked staff
group; keep PM as an option.

---

## Tier 2 — Differentiators

### 15. Anti-raid mode

The current join-burst counters only throttle the *welcome message*. Real response: detect the
burst, auto-lock the group, queue joiners for review, notify admins, auto-unlock after quiet.
This is the feature people switch bots for.

### 16. Federated / shared banlists

Global ban exists but is dev-only (`/getrekt`). Two things: let group owners federate their own
banlists across the groups they run, and optionally subscribe to external lists (CAS-style).

### 17. LLM-assisted first-message classification

Modern spam is LLM-written, contextually plausible, and defeats every keyword list. Classifying
the *first message from an unknown user* — with a local heuristic prefilter so cost stays near
zero — is the single most effective control available in 2026.

Opt-in per group, admin-reviewable, decision logged. Scope carefully: classify-and-flag first,
classify-and-act only once precision is trusted.

### 18. Settings as a Telegram Mini App

~40 callback handlers implement a nested inline-keyboard maze, with at least two dead callbacks
and missing locale strings. That pattern doesn't scale, and every setting added above makes it
worse. A Mini App settings panel replaces the whole `Commands/Menu/` tree and is the single most
visible "this bot is modern" signal.

**Build this before the other Tier 2 features**, so they land in a UI that can hold them.

### 19. Reactions

`MessageReaction` updates: detect reaction-spam, and use reactions as lightweight moderation
acknowledgement (bot reacts to a handled report instead of posting a message).

### 20. Stats and audit

Message counts exist in Redis but nothing surfaces them. Top posters, growth, action history,
exportable audit log. Also `/forgetme` for data deletion — worth having before someone asks.

---

## Tier 3 — Platform and monetisation

### 21. Telegram Stars

`Commands/Payment/` is entirely commented out and `PaymentHelper.cs` is an empty class. Stars
(`XTR`, no provider token, no `ShippingQuery`) is the current path and is far simpler than the
stub. If premium isn't a product, delete the folder and the two dead `AllowedUpdates` entries.

### 22. Rethink premium/normal

The two editions have an **identical** feature surface — no `[Command]` or `[Callback]` is gated
by `#if PREMIUM`. Differences are infrastructure only: token key, Redis offset key, tempban hash
name, looser PM-spam thresholds, the `premiumBot` allow-list gate, two hardcoded chat IDs with a
bespoke mass-add defence, and the deep-link username.

Two binaries and two deployments for no product differentiation. Either give premium real
features (classifier, federations, Mini App are natural candidates) or collapse to one bot with a
per-chat entitlement flag.

**Recommendation: collapse.** Decide this early — it affects where everything else lands.

---

## 23. Postgres migration

### Why

Redis is the only datastore, holding primary business data — warns, bans, rules, settings, report
history — with BGSAVE snapshot persistence. There's a `/halt` command that BGSAVEs before exiting
specifically because a hard kill loses data; that's a workaround for a design problem. It also
shares an instance with blackwolf on DB 0, so one misconfigured `maxmemory-policy` silently
evicts warn counts and nobody finds out until an admin complains.

### It unblocks Tier 2

Several items aren't hard on Postgres, they're effectively impossible on the current model:

| Item | Blocker |
|---|---|
| Warn decay (13) | `chat:{id}:warns` is a counter; you can't expire one warn from an int |
| Federations (16) | Many-to-many groups/federations/bans with attribution — textbook relational |
| Stats/audit (20) | Need aggregation and time series; today there's a `msgs` counter, no history |
| Classification (17) | Needs a reviewable decision log with content and verdict |

### Scope it as a split, not a move

Some current Redis usage is Redis being used *correctly* and should stay: `spam:{chatId}:{userId}`
(6s TTL flood window), `spam:added:*` join-burst counters, `chat:{id}:adminses:{userId}`,
`spammers{userId}`, the elevation and pingall cooldowns, and the update offset. Moving those
makes them worse.

### Cost

~320 Redis call sites and no data access layer — every key is an inline `$"..."` at the call site.
The repository layer plus key-name constants (Tier 0 item 6) stops being nice-to-have hygiene and
becomes the prerequisite. Then domain-by-domain behind that interface, with a dual-write and
backfill window for live data.

### The part that changes the calculus

With Redis on localhost, ~330 blocking `.Result` calls cost ~0.1ms each and the design survives.
Against Postgres query latency, that's thread-pool starvation.

**The Postgres migration forces the async refactor that `CLAUDE.md` deliberately deferred.** You
can't do one without the other, and that roughly doubles the item.

### Deployment fallout

DeployBot extracts an image and restarts a unit; there's no schema concept. `/rollbackenforcer`
already doesn't roll back the shared language directory and won't roll back schema either — so
migrations become forward-only by policy. Postgres also needs its own backup story, which the
tmpfs-logs, snapshot-Redis setup doesn't have.

### Side benefit

The premium/normal split is currently expressed as key names (`tempbanned` vs
`tempbannedPremium`). In Postgres that's an entitlement row, which makes item 22's collapse
substantially easier.

---

## Verified code findings

Read directly from source, with locations.

- **`Methods.cs:577`** — `IsGroupAdmin` reads cached `chat:{group}:adminses:{user}` *before* the
  API. Only `"true"` short-circuits (`:581`); `"false"` falls through and re-queries, so the
  cached-false write (`:595`, 5min TTL) is wasted. Cached-true is 10min TTL (`:590`).
- **`Methods.cs:581`** — hardcodes `user == 1087968824` (`GroupAnonymousBot`) as unconditionally
  admin. So anonymous admins *are* supported, crudely. It works because only admins can post
  anonymously, but it can't tell *which* admin, making `/auth` checks and log-channel attribution
  meaningless for them.
- **`ElevationSystem.cs:38`** — writes `adminses` `"true"` with **no TTL** on `/auth`. Once
  auth'd, `IsGroupAdmin` returns true permanently — after demotion, after leaving the group —
  until an explicit `/deauth`. `:83` writes `"false"` with no TTL on deauth; `:46` uses a 30-min
  TTL for `/elevate`.
- **`FlagCommands.Admin`** reads `settings/Flagged` but the menu toggle writes `settings/Report`.
  They never meet, so **reporting cannot be disabled** through the menu.
- **`/rektlist`** scans a bare `globalBan` key that nothing ever writes, so it always returns
  empty. The real data is in `globalBan:{userId}`.
- **`chat:{id}:tempmuted`** is written but never read; there's no unmute expiry job. Temp mutes
  rely entirely on Telegram's own `untilDate`.
- **`chat:{id}:banned`** is read by `Methods.IsBanned` but never written.
- **`chat:{id}:bannedlist`** type collision: `AddBanList` writes it as a string, `UnbanUser` calls
  `SetRemove` on it.
- **Setting polarity is inverted** — `"yes"` means the feature is *off* for `Flood`, `Welcome`,
  `MuteOnJoin`, `DeleteLastWelcome`. Menu icon logic compensates, but stored values read
  backwards, and anyone adding a setting will get it wrong.
- **No key-name constant table.** `spammers{id}` (no colon), `chat:{id}links` (no colon),
  `{chatId}:userJoin` vs `chat:{chatId}:userJoin`, and `chat:{userId}` holding a per-*user*
  message count.
- **`UpdateHandler.cs:94-95`** tests the `bot:bannedGroups` set against **both** `Chat.Id` and
  `From.Id` — the user check reuses the groups set. Almost certainly a bug.
- **Schema migrations** run via marker sets (`lenghtUpdate3`, `dbUpdate:lenghtUpdate` etc.)
  checked inside `CollectStats` on **every message**, forever, in the hot path.
- **`PreCheckoutQuery` / `ShippingQuery`** are in `AllowedUpdates` but dropped by
  `if (update.Message == null) return;` in `UpdateReceived`, after the offset is already written.
- **Two spam systems.** `UpdateHandler.SpamDetection`'s message-ageing code is commented out
  (~`:543-549`), so counts are cumulative for the process lifetime. `AddCount` is only ever called
  from the `@admin`/`@pingall` branch, so it's an @admin-report flood guard, not a general spam
  detector.

---

## Correction — do not repeat this error

During review it was claimed that `NewChatMembers` "doesn't fire for join-by-invite-link at scale,
doesn't fire for approved join requests". **That is wrong.** Confirmed from production that it
fires for both.

The justification for `ChatMember` / `MyChatMember` is **not** join detection. The real
justifications are:

1. `MyChatMember` for the bot's own removal and rights changes.
2. `ChatMember` for **state divergence** — when a human admin or another bot bans, restricts or
   promotes someone, Enforcer never hears about it and its Redis bookkeeping
   (`chat:{id}:muted`, `:tempbanned`, `:mutedJoiners`) silently drifts from Telegram's actual
   state. `left_chat_member` is unhandled too.
3. Proper admin-cache invalidation, replacing the 5/10/30-minute TTL guesswork and fixing the
   non-expiring `adminses` write.
4. Invite-link attribution via `ChatMemberUpdated.InviteLink` — anti-raid forensics only, so a
   Tier 2 dependency, not a Tier 0 one.

---

## Sequencing

1. **Tier 0** as one release. Little user-visible change beyond "it stopped being broken in topic
   groups", but everything else depends on it.
2. **Join gating, new-user restrictions, blacklist** (7, 8, 9) — biggest anti-spam win per unit of
   effort, all implementable on Redis.
3. **Purge/clean, link policy, report rework** (10, 11, 14) — admin quality of life.
4. **Mini App** (18), *before* Tier 2 features, so they get a UI that can hold them.
5. **Postgres migration** (23) plus the forced async refactor.
6. **Warn decay** (13) as the first thing built on the new schema. Doing it on Redis means a JSON
   timestamp array, i.e. Postgres with extra steps.
7. **Anti-raid, federations, classifier** (15, 16, 17).
8. **Stars / premium decision** (21, 22) — but decide 22 early.

---

## Explicitly out of scope

Business accounts, stories, gifts, checklists, paid media, boosts. All real Bot API surface, none
of it relevant to group moderation.

Also: **no global refactor.** The ~330 blocking `.Result` calls and fire-and-forget
`new Task(...).Start()` pattern stay until that code is being touched anyway. Two exceptions — the
send-helper layer in `Utilities.cs`, which Tier 0 item 1 forces into properly regardless, and the
whole-codebase async work that item 23 forces later.
