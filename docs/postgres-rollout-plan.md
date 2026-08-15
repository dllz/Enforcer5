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
| B5 | **Bot-wide counters, same split.** `bot:general` is incremented from paths with no routing at all. | **Decision recorded, not implemented.** Freeze in Redis for the duration and recompute once at cutover - see section 12.2. |

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
  collision **spins forever**. Reachable only because the hash is global.
- `Methods.cs:87,861,894` - `bot:general` increments bypass storage routing entirely.

Neither is fixed; both are recorded rather than silently carried.

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
   `account_message_totals` and `bot:general` during mixed state. "Sum both stores at read time" and
   "freeze in Redis until Stage D" are both defensible; silent drift is not.
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

Verified on the full dataset after a complete load:

```text
accounts loaded                4,713,977
total messages             1,168,646,843
unattributed                  26,384,222   2.258%
accounts with a residual          39,963
fully attributed               4,674,014

invariant message_count = SUM(per-group) + unattributed_count
  accounts checked             4,713,977
  holds                        4,713,977
  violated                             0
```

Going forward the hot path can keep incrementing Redis as a write cache and batch-sync to
PostgreSQL, which is the shape you described - the constraint is only that the *migration* must not
derive the starting value.

### 12.2 `bot:general` - freeze, do not reconcile

```text
bot:general.messages   1,331,976,572     13.8% above the sum of chat:{userId}
bot:general.groups            11,330     actual groups with data: 38,152
bot:general.users            215,822     actual user records:  5,126,029   (24x out)
```

`messages` follows the same superset ordering, but `groups` and `users` have drifted so far they are
a view of nothing. `Methods.cs:87,861,894` increment `bot:general` with no storage routing at all.

**Decision: freeze `bot:general` in Redis for the duration, then recompute once at cutover or retire
it.** It is a vanity counter; reconciling it is not worth the effort and pretending it is accurate
would be worse.

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
| - | The real loader. What exists is a benchmark prototype in `tools/`, not wired into `MigrationCommand`, with no resumable checkpoints or run tracking | **Not built** |
| - | Per-group state machine, `migbuf`, crawler - the whole of Stage C | **Not built** |
| - | Sanction `active`-state remodel, without which the loader can resurrect an unban | **Already satisfied.** `sanctions` and `global_sanctions` were always status rows (`active`/`due`/`revoked`/`completed`) and are never hard-deleted - verified, no `DELETE FROM` exists for either. |
| H6 | `ban:{userId}` (121,061 keys) still not migrated by anything | **Fixed.** `account_moderation_counters` plus a reader, repository and backfill path. Absolute values are carried, never increments, so a re-run converges. Regression tested. |
| - | Operations domain migration has never completed a full run | **Outstanding since the original handoff** |
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
