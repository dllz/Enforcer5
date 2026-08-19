# Code review scope — PostgreSQL migration work

Written at the end of the build session that produced commits `708b661..HEAD` on
`feature/postgres-migration`. This is the input for a review in a fresh context.

## Why a review is warranted

Nine silent data-loss or correctness defects were found during this work. **Four were in code the
rollout plan already recorded as "Fixed. Regression tested."** The orphan-group defect alone was
still live in **four separate places** after being marked closed.

The pattern is not carelessness. It is that a fix was made in one helper and never traced to its
call sites, and nothing exercised the path end to end. That pattern is the thing to review for, and
it is more likely to recur in the code written *this* session than to have been eliminated by it.

## Highest-value review targets, in order

### 1. Call-site tracing for every "fixed" defect

The recurring failure mode. For each of these, confirm every caller uses the corrected path:

| Defect | Fixed in | Verify no caller bypasses it |
|---|---|---|
| Orphan groups (`bot:groupsid` incomplete) | `MigrationInventory.GroupIdsOrDiscoverAsync` | `MigrationCommand` (4 domains), `GroupSettingsMigrationService`, `LegacyGroupScopeReader` |
| Tempban divergence | `LegacyOperationsReader` union | anything else creating `SanctionKinds.TempBan` |
| Unmirrored group writes | per-group `GroupRedisMirrorEnabled` | every `StorageRouting.RedisMirrorEnabled` use — is it *genuinely* global tier? |
| Drain buffer appends | 26 sites | every group-scoped Redis mutation; compare against the section 19.5 table |

`grep` for `RedisMirrorEnabled` and for raw `Redis.db.` writes on `chat:{...}` keys is the fastest
way in.

### 2. Code written this session with the least coverage

- **`GroupCutoverService.CopyAsync` / `VerifyAsync`** — reuses four backfill services. Are the
  reconcile semantics per-group actually equivalent to the estate-wide ones? `VerifyAsync` returns
  on the first non-zero mismatch, so later domains are not checked when an earlier one fails.
- **`GroupDrainBuffer`** — the append-after-write paths (everything except settings) have a window
  where a crash loses a mutation. The argument is that verify catches it. That argument has never
  been tested, because no cutover has run under load.
- **`ReconciliationStaging`** — creates temp tables by interpolated name. Callers are internal and
  pass literals, but confirm no path can reach it with external input.
- **`BulkPrincipalResolver`** — interpolates `sourceSql` and sequence bases into SQL. Same question.
- **`TempBanIndexMaterialiser`** — writes to Redis. Only ever run as a dry run.

### 3. Concurrency and cross-process assumptions

- `GroupMigrationStateStore` cache coherency now relies on Redis pub/sub plus a 5-minute re-prime.
  Delivery is at-most-once. **Is a 5-minute stale window acceptable?** During it, a process writes a
  migrated group to Redis only, and the drain buffer is already deleted.
- `GroupCutoverCrawler.FastTrack` is a static queue with a separate `Interlocked` depth counter.
  Check the counter cannot drift from the queue.
- `CompatibilityOutboxWorker.DrainAsync` was added for rollback. It bypasses the leader advisory
  lock, so it can run concurrently with the background loop. Claims are `FOR UPDATE SKIP LOCKED`, so
  this should be safe — confirm.

### 4. Things deliberately left, which a reviewer should not "fix"

- The tempban key shape stays as it is. Decided; see section 21.3.
- Long tempban expiries are carried unchanged, recorded as warnings. Decided; section 21.3.
- `bot:general` is frozen, not retired. Decided; section 12.2.
- The user tier loads in one pass, not per user. Decided; section 5.5.
- ~330 blocking `.Result` calls and static mutable state predate this work and were deliberately not
  touched.

## Known-weak tests

- **`RollbackRehearsalTests`** originally asserted on a key nothing wrote, so it passed while the bug
  was live. Every fix after that was verified by reverting it and confirming the test failed.
  **Tests written before that point have not all had this treatment.**
- Several cutover tests were rewritten to be outcome-agnostic because `Redis` is a static that stays
  connected once any `RedisFact` runs, so `PostgresFact` tests that assumed Redis was unavailable
  behaved differently in a full run than in isolation. **Other tests may have the same latent
  dependency on suite ordering.**
- `GlobalConfigScope` is a non-reentrant semaphore. A nested `Acquire` deadlocks the whole run with
  no output.

## Not proven, and not a review question

These need running, not reading:

- No cutover has been performed **under live traffic**. Every drain was empty.
- The crawler has never run as a live background timer, only via CLI.
- Stage B's quiesce sequence has never been rehearsed.
- `migrate tempban-index --apply` has never been run.
- Production Redis still has `maxmemory 0`.

## Environment

```text
enforcer-redis-609    redis:6.0.9   127.0.0.1:6389   production dump, 19,043,477 keys, 4.19 GB
redis609              redis:6.0.9   0.0.0.0:6390     near-empty, used by the test suite
enforcer-postgres-rehearsal  postgres:16-alpine  127.0.0.1:55439  db enforcer_rehearsal, schema w2_scale
```

Tests need `ENFORCER_TEST_REDIS` (use **6390**, never 6389 — the tests mutate) and
`ENFORCER_TEST_POSTGRES`.

Build: `export PATH="$HOME/.dotnet:$PATH" && dotnet build Enforcer5.slnx -c Release` (and `-c Premium`).
Baseline: 212 tests, 40 warnings Release / 42 Premium.
