# Redis to PostgreSQL rollout plan

Status: **not rolled out.** This is the executable plan, grounded in measurements taken against a
copy of the production dataset. Measurements, key populations and defect analysis live in
`postgres-migration-handoff.md`; this document is the sequence of operations.

---

## 1. What this rollout has to be true of

Three properties, in priority order.

1. **No data loss.** Every durable Redis record either reaches PostgreSQL or is deliberately and
   visibly excluded with a recorded reason. Silence is not an acceptable outcome.
2. **No overwrite of newer data.** A background load must never replace a value that live traffic
   wrote after the load read it.
3. **Reversible at every step.** Redis stays authoritative until the final flip, and stays mirrored
   afterwards.

Everything below exists to serve those three.

---

## 2. Migration unit: the group

Per-group cutover, not a global snapshot. The distribution justifies it - per-group statistics keys
are p50 **6**, p90 100, p99 2,707, max 528,056 across 38,152 groups. The median group is trivial to
move, so a cutover can hold a per-group write lock for milliseconds. Roughly a hundred outliers need
a chunked path.

This choice is what removes the global watermark, the Redis Stream mutation journal, the entity
version ledger and delete tombstones from the original design. They existed only to solve an
ordering problem created by choosing a global snapshot.

### The ordering rule that replaces them

> The snapshot loader only ever `INSERT ... ON CONFLICT DO NOTHING`.
> The live shadow path always writes the complete post-mutation state read from Redis.

The snapshot value is by construction older than any concurrent live write. If the live write landed
first the row exists and the loader skips it; if the loader landed first the live write overwrites
it. The loader cannot overwrite newer data because it never overwrites at all.

**Verified, not assumed.** With a live shadow write applied to a user, a later snapshot pass carrying
stale values for that user and a new value for an unseen user left the live write intact, inserted
the new user, and produced no duplicates.

The one case the rule does not cover is **hard deletes** - an unban between the loader's read and its
insert would resurrect the sanction. Two mitigations, both required:

- Sanctions are remodelled as `active` state rows so an unban is an `UPDATE`, not a `DELETE`.
- The tempban expiry worker, which unbans with `HashDelete`/`SetRemove` at `Methods.cs:954-955`, is
  stopped by the quiesce barrier during any load.

---

## 3. Production environment

Measured on `apollo-redis`, 2026-08-13.

```text
redis_version          6.0.9              rehearsal runs 7.4.10
maxmemory              0                  no limit configured
maxmemory-policy       noeviction
used_memory            4.18 GB
used_memory_rss        4.26 GB
total_system_memory    5.78 GB
```

Three things follow, and all of them matter.

**Redis holds 72% of the host's RAM with roughly 1.6 GB free.** That headroom is shared with the OS
and anything else on the box.

**`maxmemory 0` means there is no graceful failure mode.** With a configured limit and `noeviction`,
Redis returns an OOM error on write and the bot logs it. With no limit, the Linux OOM killer
terminates Redis instead - a total outage rather than a degraded one. **Recommendation independent
of this migration: set `maxmemory` to roughly 5 GB with `noeviction`,** so the failure is a rejected
write rather than a dead process.

**This is why the key inventory belongs in PostgreSQL.** 8.1M indexed members in Redis would have
been ~400-700 MB - a large fraction of the entire remaining headroom.

**Version gap.** `XAUTOCLAIM` is 6.2+, so it does not exist on 6.0.9; both reclaim loops were dead
and are now fixed (section 11). The benchmark figures were taken on 7.4, which changed listpack
encoding and some `SCAN` internals, so they are an optimistic bound until re-measured on 6.0.9.
Nothing in the plan needs a 7.0-only feature - the Lua, streams and consumer groups in use are all
5.0/6.0-safe, verified by running the whole test suite against a real 6.0.9 instance.

---

## 3a. Pre-flight gates

Do not begin without all of these.

| Gate | Status |
|---|---|
| Production Redis `maxmemory` and headroom recorded | **Done** - see above. `maxmemory` should be set before Stage B. |
| Disposition decided for the 7,951 orphan groups | **Resolved** - cause found and factory closed, see section 12. |
| Disposition decided for `user:{id}.msgs` | Open. Legacy counter, must never be summed into `chat:{userId}.msgs`. |
| Cross-group read paths enumerated | **Done** - section 9. |
| Counter normalisation verified | **Done** - section 12. |
| Redis measurements re-run on production hardware and on 6.0.9 | Open. |
| PostgreSQL sized for ~25M user-tier rows | Rehearsal user tier alone was 3,436 MB. |
| Rollback rehearsed on a scratch database with test bot tokens | Open. |

---

## 4. Stage A - deploy shadow, change nothing

Deploy the current build with `StorageMode=RedisPrimaryPostgresShadow` and **all four shadow flags
off**. This stage proves the binary is healthy and changes no behaviour.

Then enable shadow flags one at a time, letting each bake:
`PrincipalContentShadowEnabled`, `ModerationShadowEnabled`, `OperationsShadowEnabled`,
`GroupSettingsShadowEnabled`.

Watch after each: PostgreSQL connection count, `compatibility_outbox` depth,
`compat:group-settings:postgres-outbox` stream length, and the error chat.

**Rollback:** set the flag back to `false`. Redis is untouched and authoritative throughout.

---

## 5. Stage B - user and global tier, with a pause

This tier is migrated to completeness before any group cuts over, and it is **not** lazy.

For `globalBan:*` and `ban:*`, absence means *allowed* - a lazy miss is a silent security
false-negative. For the username index, a miss creates a duplicate identity. Completeness is a
correctness requirement, not an optimisation.

### Measured cost

```text
keyspace scan              20.4 -  46.2s
read global counters                7.1s
read users + COPY                  10.5s
merge, constraints dropped  67.8 -  81.2s
rebuild constraints         21.0 -  27.5s
record exceptions                   5.9s
                           ----------------
total                      132.7 - 178.4s
```

Two to three minutes, so this runs **with updates paused**. Pausing is lossless: the poll loop blocks
on a full channel, `bot:last_update` stops advancing, and Telegram redelivers on resume within its
24h retention.

The upper end touches three minutes. If production measures materially above that, take the
concurrent path in section 5.4 rather than a longer outage.

### 5.1 Sequence

1. Remodel sanction presence/absence as `active` state rows. Deploy. Bake.
2. Raise the quiesce barrier; confirm every writer reports quiet.
3. Full keyspace scan; persist the inventory.
4. Read the user tier at **pipeline depth 10,000** - measured as the knee; depth 100 is 5.4x slower
   and 25k/50k give nothing back.
5. Binary `COPY` into unlogged, index-free staging.
6. Drop indexes and foreign keys, merge, rebuild. This is **2.5-2.8x faster** than merging against
   live constraints (88.7-108.7s versus 244.6-268.1s) and is not optional at this size.
7. Assign `user_id` and `account_id` from reserved sequence blocks with `OVERRIDING SYSTEM VALUE`
   rather than round-tripping generated identities. This is what makes each dependent table a single
   set-based insert.
8. Record data-quality exceptions.
9. Reconcile. **Exact full compare** on sanctions and the username index; count plus sampled deep
   compare on the bulk.
10. Release the barrier.

### 5.2 Exit criteria

- `users` = the count of `user:{id}` keys. At rehearsal scale, **5,126,029** - not 4,799,242.
- Reconciliation passes twice with zero mismatches.
- Zero `blocking` rows in `migration_exceptions`. `warning` rows are expected and are the point.

### 5.3 Rollback

Truncate the user tier and re-run. Redis is untouched; nothing has been cut over.

### 5.4 If the pause proves too long

Run concurrently, relying on the section 2 ordering rule plus soft-delete sanctions plus the barrier
stopping the tempban worker. Slower to verify, no outage.

---

## 6. Stage C - per-group cutover

State machine per group: `redis -> draining -> verifying -> postgres`, with `failed -> redis`.
Persisted in `migration_group_state`; nothing equivalent exists today, as `migration_runs` is
outcome-only.

### 6.1 Inventory freshness rule

> A group may only cut over if the inventory is newer than the moment it entered `draining`.

From `draining` onward the per-group buffer captures every new key, so the inventory only has to
cover everything before that instant. `SCAN` guarantees any key present at both start and end of
iteration is returned at least once, which is exactly that guarantee.

The crawler therefore batches: one scan, then a batch of several hundred groups covered by it.
Keys deleted mid-scan leave stale entries, so **the inventory is a superset that may contain dead
keys** and the migrator verifies existence rather than trusting it.

### 6.2 Per-group sequence

1. Acquire the group lease via `MigrationMaintenanceGuard`.
2. `redis -> draining`. The mutation Lua begins `RPUSH migbuf:{groupId}` alongside the Redis write -
   the same shape as `GroupSettingsStore.cs:13-33`, which already does mutate + `INCR` + `XADD`
   atomically. **This is the only Lua change in the whole rollout.**
3. Chunked copy outside the lock, chunk cursor persisted so an interrupted group resumes.
4. Drain `migbuf:{groupId}` to empty, looping.
5. Take the write lock, drain the last entries, reconcile the group, flip to `postgres`, delete the
   buffer, release.
6. Mismatch: flip back to `redis`, log a `migration_exceptions` row, leave the PostgreSQL rows as
   garbage. No user-visible effect.

Buffer cap behaviour: **abort the group's migration and revert to `redis`**, never fail the user's
write. This deliberately differs from `GroupSettingsStore.cs:14-16`, where capacity backpressure
surfaces as a failed mutation - acceptable there, user-visible and wrong here.

### 6.3 Convergence

Traffic-triggered cutover and a background crawler run together and meet in the middle. Cold groups
are the *easiest* to migrate - no contention, no buffer to drain - so the crawler eats the dead tail
while traffic pulls the hot groups across.

- Crawler smallest-first, batched per scan generation, rate-limited.
- Traffic-triggered cutover enabled only after several thousand clean crawler groups.
- Burn-down = migrated groups / scan-derived population, reported to the deploy chat.

Old groups do **not** stay in Redis forever, and cutover is **not** only on modification. That
version never terminates and the Redis read path could never be deleted.

### 6.4 Outliers

Roughly a hundred groups exceed 100k statistics keys; the largest is 528,056. These take the chunked
path. **Freeze duration must be proportional to the drain buffer, never to group size.**

---

## 7. Stage D - flip and retire

1. Burn-down holds at zero across a full re-scan.
2. Switch `StorageMode` to `PostgresPrimaryRedisMirror`. Redis keeps being written via
   `compatibility_outbox`, so rollback remains a config change.
3. Soak. Length is a judgement call; no measurement supports a specific number.
4. Only after the soak, consider `PostgresOnly` and deleting the Redis read path.

**Rollback from D:** set `StorageMode` back. This is why the mirror exists and why the
`CompatibilityOutboxWorker` rebind fix matters - a stale one-shot binding would have silently
mirrored to the wrong database.

---

## 8. The quiesce barrier

Pausing the poll loop alone is not enough. The tempban worker unbans on a timer with a hard delete,
which is precisely the case the loader cannot tolerate.

`QuiesceBarrier` blocks new durable work and waits for in-flight work to finish, reporting
per-writer time-to-quiet so a straggler can be identified. Writers are classified by how they
recover:

| Writer | Location | Resume class |
|---|---|---|
| Telegram poll loop | `Utilities.cs:604-622` | Cursor - `bot:last_update` |
| Tempban expiry | `Methods.cs:932-990` | Time - re-reads the whole hash each tick |
| Spam detection | `UpdateHandler.cs:585-600` | Ephemeral - in-memory, 10-min TTL markers |
| `PostgresTempBanDueWorker` | `PostgresTempBanDueWorker.cs:55` | Time - claims are durable and re-claimed |
| `TelegramActionQueue` | `TelegramActionQueue.cs:310-340` | Cursor - stream consumer group |
| Group-settings shadow writers | `GroupSettingsStore.cs:346` | Cursor - stream consumer group |
| Redis rollback mirror | `CompatibilityOutboxWorker.cs:54` | Cursor - claimed outbox rows |
| Staged-update restart | `Updater.cs:70` | Time - blocked outright; a restart mid-migration is the worst outcome available |

**All eight are wired.** `QuiesceAsync` reports per-writer time-to-quiet, which is also the
measurement for benchmark 7 - quiesce-to-quiet latency - once run against production.

---

## 9. Cross-group reads - enumerated

This was the one item that could have invalidated group-as-unit. It has now been enumerated across
all runtime code.

**The approach survives.** The scenario that would have killed it - pervasive many-group fan-out on
the hot path - does not exist:

- **No broadcast, no announce, no leave-all, and no command that iterates groups.** `/leave`
  (`GACommands.cs:439`) takes one chat id; `/rektlist` (`GACommands.cs:454`) scans user-tier data.
- **Global bans have no fan-out.** `/getrekt` writes exactly one user-tier record
  (`GACommands.cs:267`) and enforcement is entirely lazy and per-group at
  `Methods.cs:775-848`, banning only in the chat the message arrived in.
- **Log chats and reports are cross-*chat*, never cross-*group record*.** `Service.cs:447-503` reads
  group A's own `logchat` setting and *sends a Telegram message*; it never writes a record owned by
  the destination. Reports go to admins' private chats and stay owned by the originating group.
- **No stats aggregation over groups** anywhere in runtime code.

What does stand in the way is narrower and fixable.

### 9.1 Blockers for Stage C

| # | Finding | Why it blocks |
|---|---|---|
| B1 | **Global tempban hash and a worker chosen once at startup.** The hash spans every group and has no group dimension, and `Program.cs` picked *either* `CheckTempBans` *or* `PostgresTempBanDueWorker` once. A flipped group's tempbans were left with no worker - users stayed banned forever. | **Fixed.** Both workers run, each filtering on per-group state so they partition the estate exactly once. |
| B2 | **`MigrationMaintenanceGuard` is a single global lock** requiring both bot processes stopped, so the per-group lease section 6.2 assumes did not exist. | **Fixed.** `migration_group_state` and `GroupMigrationStateStore`. |
| B3 | **`StorageMode` is process-global with 78 branch sites**, so one enum decided Redis-vs-PostgreSQL for every group. | **Fixed.** `StorageRouting`, all sites converted. See section 14. |
| B4 | **User lifetime message total splits across stores.** `OperationsStore.cs` increments `chat:{userId}.msgs` or `account_message_totals` based on the *group's* route, but the row is user-scoped. | **Fixed.** `RecordMessageAsync` routes both counters on the group, so they always move together and the invariant in section 12.1 holds. |
| B5 | **Bot-wide counters, same split.** `bot:general` is incremented from paths with no routing at all. | **Fixed.** Frozen - all six increment sites removed, values left in place, recomputed once at cutover. See section 12.2. |

### 9.2 Handled, but needing explicit work

- **H5 - inline tempban query.** `InlineMethods.cs:20-110` lists every group where the querying user
  is tempbanned, with no group context available. This is the **only user-facing feature that must
  read both stores and merge**. It is a union of disjoint sets rather than an aggregation, so it is
  cheap to do correctly.
- **H6 - `ban:{userId}` is not migrated at all.** `Methods.cs:587-590` writes and `:722-730` reads a
  user-tier hash of ban counters, with **no routing on either side** and **no reader or writer
  anywhere under `Storage/`**. Section 5 names `ban:*` as Stage B work, but no migration code
  touches it. This is a gap in the plan, not just the code.
- **H4 - callback menus.** ~45 sites handle a message in a private chat while acting on group A,
  recovering the target from `args[1]`. All are correctly parameterised today. **The route must key
  on `args[1]`, never on `call.Message.Chat.Id`** - a naive "route by arriving chat" refactor breaks
  every one of them.
- **H2 - log chat foreign key.** `EnforcerDbContext.cs:201` gives `LogChatId` an FK to
  `TelegramChatEntity`. If group A cuts over while its log destination is unmigrated,
  `PrincipalResolver.EnsureChatAsync` must create the referenced row on demand. **Unverified** -
  confirm before Stage C.
- **H7 - single-group migration still does full-keyspace work.** `LegacyOperationsReader.cs:54-73`
  `HGETALL`s the entire global tempban hash and filters client-side; `:161-200` runs a full `SCAN`
  of `*:users:*` and filters. Correct but O(keyspace) per group. A throughput blocker for the
  crawler.
- **H8 - 135 raw `Redis.db.` call sites** in runtime code, several unrouted on paths that also have
  a Postgres branch (`BanCommands.cs:420`, `ElevationSystem.cs:108`, `AdminCommands.cs:938,980`).
  Each needs classifying as global-tier, group-tier-routed, or ephemeral before Stage C.

### 9.3 Benign

`logChatGroups` is **write-only with no reader anywhere** - a dead index. `bot:groupsid` has **no
runtime iteration**; every enumeration is migration tooling. `bot:e5groupsid`, `bot:users` and
`bot:lookaround` are write-only. `spam:added:{userId}` is deliberately cross-group but ephemeral and
Redis-only for everyone, so no mixed state arises.

### 9.4 Two bugs found on the way

- `BanCommands.cs:452-456` - the tempban slot-collision loop discards the result of
  `dataUnbanTime.AddSeconds(1)`. `DateTime` is immutable, so `unbanTime` never changes and a real
  collision **spins forever**. Reachable only because the hash is global. **Fixed.**
- `Methods.cs:87,871,904` plus `UserCommands.cs:103` and `OperationsStore.cs:241` - `bot:general`
  increments bypass storage routing entirely. **Fixed** by the freeze in section 12.2. The earlier
  revision of this document cited `861,894`; those line numbers were stale by ten and it missed
  `UserCommands.cs:103` entirely, which was the sole writer of the worst-drifted field.

---

## 9a. Other risks

| Risk | Handling |
|---|---|
| Dual read path lives for the whole migration | Route-table shape already exists (`RoleStore.cs:69-75`, `PolicyStore.cs:49-55`, `ReportStore.cs:16-22`); the key changes from global mode to per-group state |
| `migbuf` memory on a memory-constrained Redis | Cap-and-abort, not cap-and-fail. Needs the `maxmemory` figure. |
| Rehearsal hardware unrepresentative | Re-measure on production before fixing any pause length |
| Group-statistics `msgs` values unmeasured | The COPY benchmark wrote id pairs only; reading 7.4M values adds ~11s at the measured rate |

---

## 10. Remaining work before Stage C

In priority order, from the cross-group enumeration:

1. **B1** - make the tempban expiry hash group-scoped. The per-group keys
   `chat:{g}:tempbanned` and `chat:{g}:tempbanned:{u}` already carry the same data
   (`BanCommands.cs:484-486`), so the global hash is a redundant secondary index. Then run both
   expiry workers concurrently, each filtered to its own population.
2. **B3** - split `StorageMode` into global-tier mode plus per-group state, then convert the ~80
   branch sites. The seven listed in 9.1 need design, not mechanical conversion.
3. **B2** - build `migration_group_state` and a real per-group lease.
   `MigrationMaintenanceGuard` cannot be reused as-is.
4. **B4/B5** - decide explicitly what happens to `chat:{userId}.msgs`,
   `account_message_totals` and `bot:general` during mixed state. **Done.** `chat:{userId}.msgs` is
   carried, not recomputed (12.1); `bot:general` is frozen (12.2).
5. **H6** - resolve `ban:{userId}`: migrate it in Stage B or record a decision to leave it in Redis.
6. Sanction `active`-state remodel (section 2).
7. `migration_key_inventory` and `migration_scan_generations` schema.
8. Promote the benchmark loader prototype into the real staging/COPY loader.
9. Crawler and per-group state machine.
10. Answer the section 3 gates.

Stage A and Stage B are **not** blocked by any of the above; they need only items 6 and 8, plus the
section 3 gates.

---

## 11. Data-loss defects found and fixed

All three were silent - no error, no log, no failed run.

| Defect | Scale | Status |
|---|---|---|
| `LegacyPrincipalContentReader` discarded users with neither name nor username | **326,787 users, 6.38%** | **Fixed.** All `user:{id}` keys migrate; a missing name is recorded as a `warning` exception, never grounds for deletion. Regression tested. |
| Group discovery seeded only from `bot:groupsid` | **7,951 groups** with data | **Fixed.** Discovery unions the set with a scan. Regression tested. |
| `chat:{userId}` treated as group-scoped | 4,752,053 keys | **Documented.** It is the user's global lifetime total, written by `OperationsStore.cs:199-204`. No group owns it, so a group-scoped migration alone would leave every lifetime count at zero. |

A missing `name` is an upstream bug - Telegram always supplies `first_name`. A missing `username` is
not a defect at all; 40.6% of users legitimately have none. Conflating the two is what caused the
first defect, and they are now handled separately.

---

## 12. Materialised views and counters

Design direction: the global structures exist only because Redis cannot aggregate. PostgreSQL can,
so they should not survive as independent stored state. That is right for the *destination* model -
but the measurements below show two of them cannot simply be recomputed at migration time, because
they hold history the group-level data no longer has.

### 12.1 `chat:{userId}.msgs` - carry it, do not recompute

Measured across the whole dataset, comparing the global counter with `SUM` of the per-group
counters:

```text
users present in both                    4,717,317
  exact match                            4,685,574   99.33%
  global HIGHER than the sum of groups      31,743    0.67%
  global LOWER  than the sum of groups            0    0.00%
users present only in group statistics            0
users present only as chat:{userId}          34,736
```

**The invariant `chat:{userId}.msgs >= SUM(groups)` holds for 100% of 4.7M users and is never
violated.** `OperationsStore.cs:197-205` increments both together in one operation, so they can only
diverge when a per-group key is deleted and the global one survives.

Recomputing the user total from group sums would therefore **lose 27,559,870 messages - 2.36% of all
volume** - and zero **34,736 users** outright, all of whom have non-zero counts.

**Decision: `chat:{userId}.msgs` is authoritative and is migrated as the user's total. The group
sums are a breakdown of it, not its source.**

**Implemented.** `account_message_totals` gains an `unattributed_count` column
(migration `20260813170212_UnattributedMessageTotals`) holding the residual that no surviving group
accounts for, with check constraints keeping it non-negative and no greater than the total. The
loader computes it set-wise from the staged group counters.

Verified on the full dataset after a complete load by the real loader (2026-08-15):

```text
accounts loaded                4,752,053
total messages             1,170,249,076
unattributed                  27,559,870   2.355%
accounts with a residual          66,479

invariant message_count = SUM(per-group) + unattributed_count
  accounts checked             4,752,053
  holds                        4,752,053
  violated                             0
```

Three independent cross-checks land exactly, which is the reason to believe the figures rather than
merely record them:

- `total messages` equals the measured `sum of chat:{userId}` of **1,170,249,076** to the message.
- `SUM(group_account_message_totals)` equals the measured `sum of group-level` of **1,142,689,206**.
- The difference between those two is **27,559,870**, which is exactly the residual the loader
  computed independently - and exactly the figure this section predicted would be lost if the total
  were recomputed from group sums.

**These supersede an earlier set of figures** (4,713,977 accounts / 1,168,646,843 messages /
26,384,222 unattributed). That load seeded its population from `user:*` alone and so dropped the
**38,076 users who exist only as a `chat:{userId}` counter**, along with their 1,602,233 messages.
The earlier numbers were self-consistent, which is precisely why the gap was invisible: the
invariant held across the subset that was loaded. The population is now the union of `user:{id}` and
`chat:{userId}`, taken from the keyspace inventory, which makes the union explicit rather than
implicit.

Going forward the hot path can keep incrementing Redis as a write cache and batch-sync to
PostgreSQL, which is the shape you described - the constraint is only that the *migration* must not
derive the starting value.

### 12.2 `bot:general` - frozen

```text
bot:general.messages   1,331,976,572     13.8% above the sum of chat:{userId}
bot:general.groups            11,330     actual groups with data: 38,152
bot:general.users            215,822     actual user records:  5,126,029   (24x out)
```

`messages` follows the same superset ordering, but `groups` and `users` have drifted so far they are
a view of nothing. `groups` is worse than drifted - **nothing in the current codebase writes it at
all**, and the reader does not read it. It is a fossil.

**Decision: freeze `bot:general` in Redis for the duration, then recompute once at cutover or retire
it.** It is a vanity counter; reconciling it is not worth the effort and pretending it is accurate
would be worse.

**Implemented.** All six increment sites are removed. The values stay in Redis untouched - freezing
means "stops moving", not "deleted".

| Site | Field | Covered instead by |
|---|---|---|
| `Methods.cs:87` | `kick` | `RecordSanctionAsync` on the line above |
| `Methods.cs:871` | `ban` | `RecordSanctionAsync` on the line above |
| `Methods.cs:904` | `ban` | `RecordSanctionAsync` on the line above |
| `UserCommands.cs:103` | `users` | nothing - it only ever counted bare `/start` in a private chat, with no de-duplication, which is why it reads 24x low |
| `OperationsStore.cs:241` | `messages` | `bot_statistic_baselines.message_count` |
| `CompatibilityOutboxWorker.cs:249` | `messages` | the mirror no longer projects it |

`bot_statistic_baselines` is kept. Two consequences worth stating plainly:

- The baseline seed is now `ON CONFLICT DO NOTHING` and returns whether it landed. It was an
  absolute overwrite, which meant every backfill run with global data included **reset the
  `message_count` that `IncrementMessageAsync` had been accumulating** - the counter sawtoothed.
- The corollary is an ordering requirement: seed before runtime traffic accumulates, or the row
  already exists and the frozen figure is not carried. The return value surfaces that instead of
  hiding it. This is tolerable only because the counter has no reader and is recomputed at cutover.

Two things were found while implementing it, neither of which is about `bot:general`:

- **The statistics mirror was entirely broken.** `CompatibilityOutboxWorker.cs:249-254` read
  `botMessages`, `userMessages` and `groupMessages` from the payload, but the producer at
  `OperationRepositories.cs:425` emitted only `{ edition, userId, groupId, occurredAt }`. All three
  casts would have thrown on the first entry. The path had never been exercised because it only runs
  under `PostgresPrimaryRedisMirror`. The producer now reads the resulting canonical totals inside
  the same transaction and emits them - absolute values, not deltas, so a replay after a crash
  converges instead of double-counting.
- **`LegacyOperationsReader` filed every baseline under `Editions.Normal`**, hardcoded, so a premium
  backfill wrote its figures to the normal row. Now uses `OperationsStore.CurrentEdition`.

Pinned by `BotGeneralFreezeTests`, which is the first test of any kind on this key.

### 12.3 `bot:groupsid` - droppable, once one caller changes

It is a materialised set of "all groups". Its only runtime consumer is
`PrincipalContentStore.GroupExistsAsync` (`:79`), used by `Service.BotAdded` to decide new-versus-
returning. Under PostgreSQL that is a row lookup on `groups`, so the set becomes redundant and can
be dropped after cutover.

It was also the source of the 7,951 orphans, and the cause was **not** groups the bot left - the set
is add-only, with no `SetRemove` anywhere. The `SADD` lived only in `IntiliseSettings`
(`Service.cs:270`), reached only from the "brand new group" branch of `BotAdded`, while settings
could also be created by `MainMenu.genMenu` (`MainMenu.cs:64-71`, auto-repairs a group with fewer
than 9 settings) and by `/resetSettings` (`ElevationSystem.cs:191`) - neither of which registered the
group. Any group that first materialised through those paths was permanently invisible.

92% of the orphans hold a configured settings hash; 74% were last active before 2019, but 7.3% were
active in 2026, so the factory was still running.

**Fixed:** `GenerateSettings` now registers the group itself, so every path that creates settings
also registers it. `SADD` is idempotent, so this is a no-op on the original path.

**Separately, and worse:** because `GroupExistsAsync` *is* the set membership test in Redis-primary
mode, an orphan that re-added the bot was treated as brand new, and `GenerateSettings` overwrote its
entire configuration with defaults. That is a live user-facing data-loss bug affecting those 7,951
groups, now closed by the same fix.

---

## 13. Readiness

**Stage A is ready to deploy. Stages B, C and D are not.**

Stage A means: deploy this build with the shipped defaults - `RedisPrimaryPostgresShadow` and all
four shadow flags `false`. With no rows in `migration_group_state` the routing is byte-identical to
the process-wide mode, so behaviour is unchanged. That is pinned by `StageADefaultsTests` rather
than asserted.

It carries three fixes that are live faults on the deployed `Migration` branch today:
the tempban slot-collision loop that spins forever, the orphan factory that leaves groups invisible
to `bot:groupsid`, and the settings-overwrite that wipes an orphaned group's entire configuration
when the bot is re-added.

### Ready

| Item | State |
|---|---|
| Both configs build, both Docker images build | Clean |
| Test suite | 127/127 against Redis **6.0.9** (production version, 3 consecutive runs) and 7.4.10 |
| Data-loss defects found and fixed | 326,787 users; 7,951 groups; orphan factory closed |
| Counter normalisation | Verified across the full dataset; invariant holds for 4,713,977 of 4,713,977 accounts |
| Cross-group read paths | Enumerated; group-as-unit validated |
| Quiesce barrier | Built, 8 writers wired, tested |
| Benchmarks 1-4 and 2b | Measured; pause decision made |
| Rollout plan | This document |

### Hard blockers

| # | Blocker | State |
|---|---|---|
| B1 | Global tempban hash has no group dimension. On flip, a group's in-flight tempbans were left with no worker - users stayed banned forever | **Fixed.** Both expiry workers now filter on per-group state and partition the estate: `Methods.CheckTempBans` skips groups PostgreSQL owns, `ClaimDueTempBansAsync` claims only those groups. Regression tested. |
| B2 | No per-group lease. `MigrationMaintenanceGuard` is one global lock requiring both bots stopped | **Fixed.** `migration_group_state` plus `GroupMigrationStateStore` provide an exclusive, expiry-reclaimable per-group lease; only the holder may advance a group. Regression tested. |
| B3 | `StorageMode` is process-global across ~80 branch sites; per-group routing does not exist | **Fixed.** `StorageRouting` splits global-tier from per-group routing; all 78 call sites converted. The only remaining `StorageModeProvider` uses are the three backfill guards that legitimately require shadow mode. Regression tested. |
| - | The real loader. What exists is a benchmark prototype in `tools/`, not wired into `MigrationCommand`, with no resumable checkpoints or run tracking | **Built.** `Storage/Loading/`, wired as `migrate inventory` and `migrate user-tier backfill`, resumable, run-tracked. Verified end to end on the full rehearsal dataset - section 16. |
| - | Per-group state machine, `migbuf`, crawler - the whole of Stage C | **Not built** |
| - | Sanction `active`-state remodel, without which the loader can resurrect an unban | **Already satisfied.** `sanctions` and `global_sanctions` were always status rows (`active`/`due`/`revoked`/`completed`) and are never hard-deleted - verified, no `DELETE FROM` exists for either. |
| H6 | `ban:{userId}` (121,061 keys) still not migrated by anything | **Fixed.** `account_moderation_counters` plus a reader, repository and backfill path. Absolute values are carried, never increments, so a re-run converges. Regression tested. |
| - | Operations domain migration has never completed a full run | **Done.** Completed twice against the full rehearsal copy - 54m42s then 38m39s - with zero blocking errors and identical counts, so it converges rather than duplicating. See section 17. |
| - | Reconciliation never run twice clean across all domains | **Outstanding** |
| - | Rollback never rehearsed | **Outstanding** |

### Measurement caveat

Every benchmark figure was taken on Redis 7.4.10 and PostgreSQL in ARM Docker on macOS. Production is
Redis **6.0.9** on x86 with 4.18 GB of 5.78 GB RAM in use. Treat the numbers as ratios and optimistic
bounds; re-measure before committing to any pause window.

### What could ship now

Stage A only: deploy with `StorageMode=RedisPrimaryPostgresShadow` and all four shadow flags
`false`. That changes no behaviour, and it carries the three production bug fixes - the tempban
collision loop, the orphan factory, and the settings-overwrite on re-add.

### Per-group state - built

`migration_group_state` (migration `GroupMigrationState`) keys cutover state by **Telegram chat id**,
not the internal group id, because a group's state must be resolvable on the message hot path before
any PostgreSQL row is guaranteed to exist.

States are `Redis -> Draining -> Verifying -> Postgres`, plus `Failed -> Redis`. Only `Postgres`
counts as authoritative: `Draining` and `Verifying` still serve reads from Redis, which is what makes
a failed cutover cost nothing.

`GroupMigrationStateStore` holds a hot-path cache in which **absent means Redis-owned**, so the
common case for most of the migration is a dictionary miss rather than a query, and a cold or failed
prime degrades to pre-migration behaviour rather than stopping the bot.

The lease is exclusive per group, reclaimable once expired so a crashed migrator cannot strand a
group, and only its holder may advance the state. Lease expiry is computed by PostgreSQL rather than
the caller, so the written value and the `now()` it is later compared against come from one clock -
mixing the application and database clocks made reclaim depend on skew between two hosts, which the
tests caught.

---

## 14. Per-group routing - built

`StorageRouting` replaces direct use of the process-wide mode. It exposes three questions rather
than one:

- `GlobalTierPostgresIsPrimary` - users, accounts, the username index, global sanctions and roles.
  Migrated once, to completeness, in Stage B. Stays on Redis through the per-group phase, which is
  safe because Stage B removes nothing from Redis.
- `GroupPostgresIsPrimary(chatId)` - group-scoped data. The process-wide mode acts as a ceiling:
  once flipped, every group is on PostgreSQL regardless of per-group state.
- `GroupRedisMirrorEnabled(chatId)` - a group that has cut over still needs its PostgreSQL writes
  projected back into Redis, and the process-wide mirror flag is false throughout that phase.

`EffectiveGroupMode(chatId)` projects a group's state onto the existing `StorageMode` enum, so the
route tables in `RoleStore`, `PolicyStore` and `ReportStore` and their tests were reused unchanged
rather than duplicated per group.

**With no rows in `migration_group_state` the behaviour is identical to the process-wide mode**, so
deploying this changes nothing until groups are deliberately flipped. That is pinned by a test.

Classification of the 78 converted sites:

```text
group-scoped   settings, content, custom commands, warnings, roles, policies,
               reports, group sanctions, per-group statistics, welcome media
global tier    user records, username index, global bans, global roles,
               user lifetime message totals, moderation counters
both tiers     RecordMessageAsync and AppendAuditAsync route on the group when
               they have one and on the global tier when they do not
```

`RecordMessageAsync` is the subtle one: it increments the user's lifetime total and the per-group
total in a single operation, so the group decides for both. The invariant
`user total = SUM(per-group totals) + unattributed` only holds if the two move together.

Two paths needed more than mechanical conversion. `Program.cs` now runs **both** tempban expiry
workers instead of choosing one at start-up. `InlineMethods.GetTempbanUserDetails` - the inline query
listing every group where a user is tempbanned, which has no group context - reads both stores and
concatenates, filtering Redis entries for groups PostgreSQL owns so a migrated group is not reported
twice.

---

## 15. Latent bugs found while converting

Both were dormant because the code paths that reach them had never been enabled.

**`FOR UPDATE` on the nullable side of an outer join.** `SanctionRepository.CompleteClaimAsync`
reads the sanction back to build the Redis mirror payload, and that query `LEFT JOIN`s the actor's
account. PostgreSQL rejects an unqualified `FOR UPDATE` there:

```text
ERROR:  FOR UPDATE cannot be applied to the nullable side of an outer join
```

So the read threw on every call, the caller treated it as an unban that could not be completed, and
released the claim instead - meaning **the tempban never expired**. Only reachable with mirroring
on, which is why it never fired. Now `FOR UPDATE OF s`, which is all that was ever needed, and
pinned by a test that exercises the mirror path.

**The per-group claim filter ignored the global ceiling.** The first version of the PostgreSQL
expiry worker's filter required a `migration_group_state` row with state `Postgres`. Once the
process-wide mode is flipped there are no such rows, so it would have claimed nothing and every
tempban would have stalled at Stage D. Fixed with an `@AllGroups` short-circuit and covered by the
mirror-mode test.

---

## 16. The loader - built

`tools/Enforcer5.Benchmarks` proved the design; `src/Enforcer5/Storage/Loading/` is the production
version. Two commands: `migrate inventory [--fresh]` and `migrate user-tier backfill`.

### 16.1 The keyspace inventory

One full pass, recorded in `migration_key_inventory` against a `migration_scan_generations` row.
Everything downstream reads it instead of re-scanning, because `SCAN MATCH` filters *after* walking
the keyspace - so a per-prefix scan costs a full pass and N prefixes cost N passes. The operations
domain pays that six times per run today.

Measured over the full rehearsal copy:

```text
keys scanned              19,407,887
unclassified                   1,330      0.0069%
elapsed                        15m28s     (7.4.10, ARM Docker on macOS)
```

Classification, which cross-checks against the independently measured key populations:

```text
group-statistics           7,383,778     matches the measured {groupId}:users:{userId} count
user                       5,126,029     matches the measured user:{id} count
user-message-total         4,752,053     matches the measured chat:{userId} count
group-scoped                 753,166
ephemeral                    719,600
report                       495,278     flagged:* plus flaggedReply:*
moderation-counters          123,459
bot-global                    51,351
global-ban                     1,843
unclassified                   1,330
```

The 1,330 unclassified keys were four shapes, and investigating them is the point of the bucket:

| Shape | Count | Disposition |
|---|---|---|
| `lang:{id}` | 1,325 | Superseded by `chat:{chatId}:language` (`Methods.cs:258`). Durable but unread, so classified `legacy` - recognised and deliberately not carried. |
| `groupBan:{chatId}` | 3 | Live, read at `Service.cs:240`. Now classified `group-scoped`. |
| `antinamelengthsettings`, `antitextlengthsettings` | 1 each | Genuine orphans - a settings hash written with no `chat:{id}:` prefix. Left unclassified deliberately; this is exactly what the bucket is for. |

`legacy` is a distinct class from `ephemeral`: ephemeral data is TTL-bounded and rebuilt, legacy data
is durable and simply no longer read. Collapsing them would hide a real decision.

### 16.2 The user tier load

```text
users staged               5,164,105     union of user:{id} and chat:{userId}
group counters staged      7,383,778
accounts created           5,164,105     users = accounts = identity links
groups created                38,152     matches the measured group population exactly
missing names                459,161     recorded as warnings, never grounds for deletion
blocking exceptions                0
elapsed                        4m32s     (7.4.10, ARM Docker on macOS)
```

The population is the **union**, not `user:*` alone. 38,076 users exist only as a message counter
with no user record, and seeding from `user:*` drops every one of them - see section 12.1.

### 16.3 What changed from the prototype

Removed outright: `ResetCanonicalAsync`, which `TRUNCATE ... CASCADE`d the whole user tier and
deleted `migration_runs` and `migration_exceptions` - the entire audit history; and the two-variant
loop that loaded the tier twice per run.

Fixed:

- **The group tier was never merged.** Staged group counters were loaded only so their `SUM` could
  feed the residual; nothing wrote `group_account_message_totals`, so every per-group total would
  have arrived at zero.
- **Constraint names were hardcoded** EF-generated strings dropped with `IF EXISTS`. An EF rename
  would match nothing, the load would silently run against live constraints at 2.5x the cost, and
  the rebuild would create an index the schema no longer expected. Definitions now come from
  `pg_catalog` and cannot drift.
- **Drop and rebuild now happen inside the load transaction.** PostgreSQL DDL is transactional, so a
  crash rolls the drops back. Dropping separately would leave a live database missing unique indexes
  with no record of what they were.
- **4.75M counters were materialised into a dictionary** before the COPY - several hundred MB on a
  host with roughly 1.6 GB free. The lifetime total is now read alongside the profile in the same
  pipelined pass.
- **Nothing was resumable.** Staging is now its own watermark: the loader resumes from the highest id
  already written rather than a cursor that could disagree with the data. The inventory persists its
  `SCAN` cursor in the same transaction as the batch it describes, so the resume point can never be
  ahead of the keys stored.
- An absent, unparseable or negative counter is written as **null, not zero**. Zero is a real value
  that would silently overwrite a genuine total.

### 16.4 Measurement caveat, restated

These figures are Redis **7.4.10** in ARM Docker on macOS against a PostgreSQL in the same
environment. Production is Redis **6.0.9** on x86. Treat them as ratios. The inventory pass in
particular is dominated by writing 19.4M rows to PostgreSQL rather than by Redis, so it will move
with PostgreSQL I/O rather than with the Redis version.

Note also that the inventory does **not** need to run inside the quiesce window: it is a documented
superset that tolerates concurrent mutation, and consumers verify existence rather than trusting it.
Only the user-tier load belongs in the pause.

---

## 17. Operations - the domain that had never finished

It has now completed a full run twice against the rehearsal copy.

```text
run 1   54m42s   errors 197   global 1,646
run 2   38m39s   errors   0   global 1,843     warnings 310
```

Run 2 wrote identical sanction, report, counter and audit counts to run 1, so the domain converges
rather than duplicating. Run 2 is faster because the upserts had nothing to change.

### 17.1 Why it never finished

Two causes, both structural.

**~12.1M single-row transactions.** `UserStatistics` (4,752,053) and `GroupUserStatistics`
(7,383,778) were written one row at a time, and each row opened a connection, began a transaction
and resolved two principals first. Those two populations are now loaded set-wise by the Stage B
loader, which also computes the unattributed residual - and the per-row upsert overwrote
`message_count` *without* recomputing that residual, so it could silently break the invariant.

All that remains of the statistics path is the last-seen timestamp from `chat:{groupId}:userlast`,
which is genuinely unique to this domain. It is applied as one `COPY` plus one set-based `UPDATE`:
7,377,061 rows.

**Six full keyspace passes per run.** `user:*`, `globalBan:*` and `flagged:*` from
`MigrationCommand`, plus a full `SCAN` of `*:users:*` inside the reader, each walking all 19.4M keys
because `MATCH` filters after the walk. All are now served from the recorded inventory.

### 17.2 Two data-loss findings

**197 global bans were being dropped.** Every one failed to parse its `time` field, and for
`globalBan` **absence means allowed** - so these were silent security false-negatives, not missing
statistics. The cause is that the field holds at least four shapes, because the value was written
with whatever culture the writing host had at the time:

```text
12/17/2024 5:37:48 PM                     month-first, handled
2020/06/18 11:44:56 AM                    year-first, handled
13/08/2019 09:05:37 AM                    day-first with meridiem     84 keys - no format matched
On Saturday, 22 October 2016\nAt 16:09:11 prose, spanning two lines   113 keys - no format matched
```

The prose shape was written by a display formatter and stored verbatim, newline included. Both are
now parsed, and all 1,843 `globalBan` keys migrate.

**310 timestamps are genuinely undecidable.** A slash date whose first two components are both 1-12
can be read either way, and the estate demonstrably contains both orderings - `12/17/2024` can only
be month-first, `13/06/2016` can only be day-first. These are resolved month-first, because that is
the dominant shape, and recorded as **warnings** rather than silently trusted. The spread is up to
eleven months on a moderation record.

### 17.3 Errors and warnings are now separate

They used to be one list, which had two bad consequences: a single questionable value failed the
whole run, *and* it silently disabled reconciliation, which skips all removals whenever any error is
present. Since the operations snapshot always had errors at full scale, operations reconciliation
had in practice never compared anything while reporting `mismatches=0`.

`Errors` now means "could not be read, not migrated" and fails the run. `DataQualityIssues` means
"read and migrated, but the source is defective or undecidable" and is recorded at `warning`
severity. That distinction is what makes the reconciliation gate in section 13 meaningful.

### 17.4 Still row-at-a-time

Reports (310,317), audit events (322,721), moderation counters (140,023) and sanctions (12,404) are
still written one row at a time, each resolving principals first. That is the remaining 38 minutes.
It completes, so it is no longer a blocker, but it is the obvious next optimisation and it is the
same staging-plus-set-based-merge shape the loader already uses.

Note also that `global_sanctions` holds 1,760 rows against 1,843 migrated keys. The 83-row gap is
consistent across both runs and is not yet explained - it should be before Stage B is called done.
