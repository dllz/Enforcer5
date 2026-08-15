# PostgreSQL migration handoff

Paused on 2026-08-11. The data model and scoped rehearsal are in good shape, but the full migration implementation is not ready for production. The remaining work is mainly migration throughput and safe snapshot/delta ordering.

## Current model

The approved PostgreSQL baseline is `20260810220328_ApprovedStorageBaseline`. Custom-command conflict storage is added by `20260811074416_CustomCommandConflictResolution`.

Important model decisions already implemented:

- Internal users, Telegram accounts, chats, and groups use independent generated IDs.
- `telegram_chats` is the universal destination table. A log destination does not create a managed `groups` row.
- Telegram profile names and usernames have temporal history.
- Legacy report identities remain nullable when Redis does not contain structured IDs. Telegram user `0` is never synthesized.
- Persisted Telegram file IDs record their owner edition: `normal`, `premium`, or `legacy_unknown`.
- Migration commands support `--group` and the scoped readers avoid global scans.
- Large principal ingestion uses bulk PostgreSQL operations.

EF Core currently owns schema modeling and generated migrations. Runtime repositories and migration code currently use Dapper/raw SQL. We discussed revisiting that architecture later, but it is not part of the current migration-performance work.

## Custom commands

Legacy Redis behavior remains case-sensitive before cutover. PostgreSQL-primary behavior is case-insensitive.

Canonical triggers:

- Remove the leading `#`.
- Normalize to Unicode NFC.
- Keep Unicode letters, decimal digits, combining marks, and connector punctuation until punctuation or emoji.
- Apply `ToLowerInvariant()`.

This supports Persian, Kanji, accented text, and composed/decomposed Unicode forms.

Legacy media labels map to canonical Telegram media types:

- `gif` -> `animation`
- `file`, `text`, `unknown` -> `document`
- `videoNote` -> `video_note`

When several Redis fields collapse to one canonical trigger, no value is selected during migration. Every source candidate is retained in `custom_command_conflict_candidates`. On first invocation after cutover, the chat gets buttons to keep one candidate or delete all. Only group admins can resolve it. Candidate rows are marked resolved rather than deleted, and both choices provide an Undo button. Undo restores the original Redis source fields and side hashes.

The full backup currently produces at least these conflicts:

- `beauty`: `#Beauty`, `#beauty`
- `deadtalk`: `#deadtalk`, `#deadtalk❗️`, `#deadtalk❗️Deadtalk`

## Rehearsal infrastructure

Local isolated containers:

- PostgreSQL: `enforcer-postgres-rehearsal`, `127.0.0.1:55439`, database `enforcer_rehearsal`, user `enforcer`.
- Redis: `enforcer-redis-rehearsal`, `127.0.0.1:6389`, no password.

The Redis container loads:

```text
/Users/daniel.lotz/Downloads/redis-backup-20260807-105430.rdb
```

RDB facts:

- Size: about 1.8 GB.
- SHA-256: `d6e9ddddff3dd1f79f68d69eff23f8868938fa661ed9b204e4352d1bde1aff9e`.
- Loaded keys: 19,043,477.
- Redis memory after load: about 4.05 GB.

Do not point another bot consumer at this Redis while testing polling. It contains production update offsets from the backup.

## Verification completed

- Release and Premium builds pass with existing warnings.
- Release and real Premium Docker artifact builds pass. The Docker build argument is `CONFIG`, not `BUILD_CONFIGURATION`.
- Full integration suite passed 91/91 when both rehearsal PostgreSQL and Redis were configured.
- Scoped group `-1001030085238` migrated and reconciled cleanly before the full-backup work.
- Custom-command selection, delete-all, retained history, and Undo have PostgreSQL integration coverage.

## Full-backup results

Successful full-domain runs from the 19-million-key RDB:

```text
Principal/content:
  users=4,799,242
  groups=36,441
  entitlements=28
  content=14,030
  canonical commands=99,821
  initial mismatches=0

Group settings:
  processed=364,410/364,410
  mismatches=0
  failures=0
  invalid groups=0

Moderation:
  roles=2,160
  elevation blocks=109
  policies=23,081
  warning balances=227,696
  initial mismatches=0
```

Principal/content took about 33 minutes. Group settings took about 87 minutes after resuming an interrupted run. Moderation took about 28 minutes.

Operations has not completed. Its `migration_runs` rows may remain `running` because tool cancellation killed the process. Those rows are abandoned rehearsal attempts, not successful validation.

## Problems found by the full backup

These issues were found and fixed:

- Unsupported legacy custom-command media labels violated the canonical check constraint.
- Full migration still used Redis `KEYS`; all migration key discovery was changed to cursor-based `SCAN`.
- Welcome media file ID and owner edition were written in separate statements, temporarily violating the ownership constraint. They are now updated atomically.

The full backup exposed the main performance defect in operations: it iterated every known user for every group. With 36,441 groups and 4.8 million users, that implied roughly 175 billion Redis probes. This path was replaced with existing-key discovery, but operations still did not finish quickly enough because it retained other expensive behavior.

Redis command stats during aborted operations runs showed millions of calls, including roughly 15 million `HGET`, 8.5 million `HGETALL`, and 1.8 million `SCAN` calls accumulated over rehearsal attempts. Operations had already written 292,768 reports and 29,809 account totals in one aborted run, but no completed operations result exists.

## Keyspace survey

Measured against the rehearsal Redis holding the production RDB (19,407,887 keys, 3.96 GB).

A full keyspace `SCAN COUNT 5000` completes in about 40 seconds wall, of which the scan itself is
0.16s user and 0.6s system client-side. Earlier planning assumed this pass cost tens of minutes and
therefore needed a global write freeze. It does not.

```text
{groupId}:users:{userId}          7,383,778   group statistics
user:{id}                         5,126,029   user identity (sparse: name/username/msgs)
chat:{userId}                     4,752,053   user global message total
user:{id}:query                     352,574   inline query state
flagged:{groupId}:{msgId}           311,406   reports
chat:{groupId}:bannedlist:{n}       186,679   group
flaggedReply:{groupId}:{msgId}      183,872   reports
ban:{id}                            123,459   global sanctions
bot:usernames:{n}                    51,320   username index
chat:{groupId}:{suffix}          29k-50k ea   group settings, deterministic
globalBan:{id}                        1,843   global sanctions
```

Per-group statistics-key distribution across 38,152 groups: p50 6, p90 100, p99 2,707,
max 528,056. The median group is trivial to migrate; roughly a hundred outliers need a chunked path.

## Data-loss defects found by the survey

Three separate silent-skip defects. None are fixed yet.

**`bot:groupsid` is not a complete group inventory.** It holds 36,441 members, but 38,152 groups have
statistics keys. 7,951 groups have data and are absent from the set; 6,240 are in the set with no
statistics. Every reader seeds from it — `LegacyGroupScopeReader.cs:49`,
`GroupSettingsMigrationService.cs:23`, `LegacyPrincipalContentReader.cs:16-19` — so those 7,951
groups are silently skipped. Only a keyspace scan yields a trustworthy group population.

**`chat:{userId}` is user-scoped, not group-scoped.** `OperationsStore.cs:199-204` increments
`chat:{userId}.msgs` (global lifetime total) and `{groupId}:users:{userId}.msgs` (per-group total) on
every message. All 4,752,053 bare `chat:{n}` keys hold positive IDs; none are group IDs; 4,713,977
correspond to a `user:{id}` record. No group owns them, so any group-scoped migration will miss them
and every user's lifetime count would read zero after cutover.

**A missing `name` is an upstream bug; a missing `username` is not.** Telegram always supplies
`first_name`, so a `user:{id}` record without a `name` field is a defective write, not a legitimate
shape. A username, by contrast, is optional and 40.6% of users legitimately have none. These two must
not be treated as one condition, which is exactly the mistake in the current reader.

Exact counts over the full 5,126,029-user population, taken from the benchmark staging table rather
than a sample:

```text
no name (upstream bug)                        421,085    8.21%
  of which username present, migrated today    94,298    1.84%   bug carried through
  of which nothing present, dropped today     326,787    6.38%   lost entirely
no username (normal, not a defect)          2,081,203   40.60%
```

`LegacyPrincipalContentReader.cs:34` keeps a user only if `name` or `username` is present. That
treats "we failed to capture a name" as "this is not a user". The 326,787 records it discards are
real users with real message history — 326,689 of them, 99.97%, carry a legacy `user:{id}.msgs`
counter. They must be migrated, with the missing name recorded as a data-quality exception rather
than used as grounds for deletion.

**`user:{id}.msgs` is a legacy field.** Nothing in the current code writes it —
`PrincipalContentStore.cs:16-20` writes only `name` and `username`. It is the pre-schema-change
predecessor of `chat:{userId}.msgs`, and it is concentrated almost entirely in the dropped
population above.

Migration rule that follows: `user:{id}.msgs` must never be summed into `chat:{userId}.msgs`. For the
transitional records holding both that double-counts. Carry it as a separate legacy-origin value or
drop it by explicit decision.

## Benchmark results

Measured by `tools/Enforcer5.Benchmarks` against the rehearsal pair holding the production dataset.
The harness is never shipped; the Dockerfile builds `src/Enforcer5` only.

Hardware caveat: ARM Docker on macOS. CPU is likely faster than the production node, disk and network
differ. Treat these as ratios and ceilings, and re-run the Redis measurements against production
under load before fixing any pause length.

### Redis read side

```text
full keyspace SCAN COUNT 5000     19,407,887 keys   20.4s cold-cache 46.2s   420k-949k keys/s
read chat:{userId}.msgs            4,752,053 keys    7.05s                   674k/s
read user:{id} + stream to COPY    5,126,029 keys   10.53s                   487k/s
COPY group statistics id pairs     7,383,778 rows    3.26s                 2,266k/s
```

Pipeline depth matters more than anything else on the read side, and **10,000 is the knee**:

```text
depth    100    users 48.74s   counters 32.24s
depth  1,000    users 13.52s   counters 11.22s
depth 10,000    users  8.99s   counters  6.86s
depth 25,000    users 10.90s   counters  6.52s
depth 50,000    users  9.84s   counters  6.52s
```

The scan's classification counts reproduce the independent `redis-cli` survey exactly - 5,126,029
users, 4,752,053 counters, 38,152 groups with statistics - which cross-validates both.

### PostgreSQL write side

One Telegram user becomes five canonical rows, so 5.1M users is roughly 25M rows. Two runs, so these
are ranges rather than single figures; run-to-run variance is 10-20%.

```text
merge staging -> canonical, indexes and FKs present     244.6 - 268.1s
merge staging -> canonical, constraints dropped first    67.8 -  81.2s
rebuild indexes and FKs                                  21.0 -  27.5s
record data-quality exceptions (421,085 rows)                    5.9s
```

**Dropping constraints, loading, then rebuilding is 2.5-2.8x faster** than loading against live
constraints - 88.7-108.7s versus 244.6-268.1s. The loader must do this.

Identities are assigned from reserved sequence blocks and inserted with `OVERRIDING SYSTEM VALUE`,
rather than round-tripping `GENERATED ALWAYS AS IDENTITY` values row by row. That is what lets every
dependent table be written with one set-based insert.

### User-tier total, and the pause decision

```text
keyspace scan             20.4 -  46.2s
read global counters               7.1s
read users + COPY                 10.5s
merge, constraints dropped 67.8 -  81.2s
rebuild constraints        21.0 -  27.5s
record exceptions                  5.9s
                          ----------------
total                     132.7 - 178.4s
```

**Between about two and three minutes.** That sits at or below the three-minute threshold set for the
decision, so the user tier should be migrated with updates paused rather than concurrently. Pausing
is lossless - the poll loop blocks, `bot:last_update` stops advancing, and Telegram redelivers within
its 24h retention - and it removes the need to rely on soft-delete sanctions and complete-state
shadow writes during the load. Those disciplines remain necessary for the per-group phase.

The upper end touches the threshold, so this must be re-measured on production hardware before a
pause window is committed to. If it lands materially above three minutes, the fallback is the
concurrent path, not a longer outage.

Not yet measured: the group-statistics COPY above writes id pairs only. Reading the 7.4M `msgs`
values adds roughly 11s at the measured counter rate.

### Correctness results from the benchmark load

The loader is a prototype of the proposed design, not a wrapper over the existing repositories, so
its output is also evidence about the current implementation.

```text
users                    5,126,029
telegram_accounts        5,126,029
user_identity_links      5,126,029
telegram_account_profiles  4,799,242
account_message_totals   4,713,977
database size                3,436 MB
```

`telegram_account_profiles` lands at exactly 4,799,242 - the same figure the earlier full-backup
rehearsal reported as "users migrated", and the same count present in `enforcer_rehearsal` today.
The difference is the whole defect: `LegacyPrincipalContentReader.cs:34` keeps a user only if `name`
or `username` is present, so **326,787 users, 6.38%, never reach PostgreSQL**. Those are records
whose name was never captured - an upstream write bug, since Telegram always supplies `first_name` -
and 99.97% of them carry a legacy message counter, so they are demonstrably real users.

The new loader migrates all 5,126,029 and omits only the profile row where there is genuinely no
profile content to record. A missing name is logged as a data-quality exception; a missing username
is not, because 40.6% of users legitimately have none.

`account_message_totals` at 4,713,977 matches the independently measured count of `chat:{n}` ids that
also exist as `user:{id}`.

The insert-if-absent rule was verified directly rather than argued. With a live shadow write applied
to a user, a later snapshot pass carrying stale values for that user and a new value for an unseen
user left the live write intact, inserted the new user, and produced no duplicates. That is the
property that makes the entity-version ledger unnecessary.

## Counter reconciliation - are the totals normalised?

Measured across the whole production dataset, not sampled. The question was whether the global
counters are a faithful materialised view of the group-level data, and therefore whether they can be
recomputed during group migration rather than carried.

**They are consistent, but they are a strict superset - not a duplicate.**

Comparing `chat:{userId}.msgs` against `SUM({groupId}:users:{userId}.msgs)` per user:

```text
users present in both                    4,717,317
  exact match                            4,685,574   99.33%
  global HIGHER than the sum of groups      31,743    0.67%   surplus 25,331,345
  global LOWER  than the sum of groups            0    0.00%
users present only in group statistics            0
users present only as chat:{userId}          34,736             total 2,228,525
```

The invariant holds without exception: **`chat:{userId}.msgs >= SUM(groups)` for 100% of 4.7M
users, and is never lower.** That is what `OperationsStore.cs:197-205` implies - both counters are
incremented together in the same operation - so the two can only diverge when a per-group key is
deleted while the global one survives.

### Consequence for the migration

Recomputing the user total from group sums **would silently lose 27,559,870 messages, 2.36% of all
volume**:

```text
surplus on users present in both       25,331,345
total of users with no group stats      2,228,525
                                       ----------
unattributable to any surviving group  27,559,870   2.36%
```

and would zero **34,736 users** outright, every one of whom has a non-zero count.

So `chat:{userId}.msgs` is authoritative and must be carried. The group sums are a breakdown of it,
not its source. Storing the residual (`global - SUM(groups)`) as an explicit unattributed bucket
keeps the two reconciling exactly and makes the invariant checkable forever.

### `bot:general` does not reconcile at all

```text
bot:general.messages   1,331,976,572
sum of chat:{userId}   1,170,249,076    global is 13.8% higher
sum of group-level     1,142,689,206

bot:general.groups            11,330    actual groups with data: 38,152
bot:general.users            215,822    actual user records:  5,126,029   (24x out)
```

The same superset ordering holds for `messages`, but `groups` and `users` have drifted so far - 24x
on users - that they cannot be treated as a view of anything. `Methods.cs:87,871,904`,
`UserCommands.cs:103` and `OperationsStore.cs:241` incremented `bot:general` with no storage routing
at all, which is part of why. (An earlier revision cited `861,894` - stale by ten - and missed
`UserCommands.cs:103`, the sole writer of `users`, the worst-drifted field of the three.)

Treat `bot:general` as a vanity counter: freeze it in Redis for the duration and either recompute it
once at cutover or retire it. Do not attempt to reconcile it.

**Done.** Frozen - every increment site removed, the values left in place, `bot_statistic_baselines`
kept for the recompute. See rollout plan section 12.2 for the site list and for two unrelated defects
the work surfaced: the statistics mirror read three payload fields the producer never emitted, and
every baseline was filed under `normal` regardless of edition.

Do not continue optimizing by adding unbounded task parallelism to row-at-a-time code. It will move the bottleneck to PostgreSQL locks, connection pooling, WAL, and memory.

The migration should be redesigned around these steps:

- Inventory relevant Redis keys once.
- Classify every durable source key by domain.
- Hydrate values with bounded concurrency and visible progress.
- Stream chunks into PostgreSQL staging tables with binary `COPY`.
- Resolve already-migrated principals with set-based joins.
- Merge staging data into canonical tables in bounded transactions.
- Reconcile with set operations rather than one query per record.
- Record chunk checkpoints so interrupted runs resume.

The first operations optimization was started in `LegacyOperationsReader`: group-user statistics now discover existing `group:users:user` keys instead of probing the global user set for each group, and global user reads were partially pipelined. This is not the final design and has not completed a full benchmark. Review it before building further.

Group settings also needs a bulk path. The current implementation performs ten Redis observations per group and millions of PostgreSQL statements. The immediate safe improvement is observing all ten Redis hashes concurrently and resolving each group once, but the target design should stage complete group snapshots and merge them set-wise.

Principal/content is currently acceptable at about 33 minutes, but still writes groups, content, entitlements, and commands row by row after bulk user ingestion. Optimize it only after operations and settings.
## Safe live migration design

**Superseded.** The original design here - a global RDB snapshot, a watermark `W`, a Redis Stream
mutation journal, a `migration_entity_versions` ledger and delete tombstones - existed only to solve
an ordering problem created by choosing a *global snapshot* as the migration unit.

Choosing the **group** as the migration unit removes that problem, and the ledger collapses to a
conflict clause:

> The snapshot loader only ever `INSERT ... ON CONFLICT DO NOTHING`.
> The live shadow path always writes the complete post-mutation state read from Redis.

This was verified empirically, not assumed - see the correctness results above.

The full staged plan now lives in **`postgres-rollout-plan.md`**.

## Production status

Do not roll this migration to production yet. See `postgres-rollout-plan.md` for the pre-flight
gates and the staged sequence.

Fixed since the original handoff:

- 326,787 users (6.38%) silently discarded by the principal reader - now migrated, with a missing
  name recorded as a `warning` rather than used as grounds for deletion.
- 7,951 groups absent from `bot:groupsid` - discovery now unions the set with a scan, and
  `GenerateSettings` registers the group so the orphan factory is closed.
- `XAUTOCLAIM` used against Redis 6.0.9, where the command does not exist - both reclaim loops were
  dead. Replaced with an `XPENDING`+`XCLAIM` path valid from Redis 5.0.
- A tempban slot-collision loop that could spin forever.

Still required before any rollout:

- The blockers in `postgres-rollout-plan.md` section 9.1, chiefly the global tempban hash.
- Staging/COPY loader promoted from the benchmark prototype, with resumable checkpoints.
- A full operations migration benchmark.
- All domains rerun from a clean PostgreSQL database, reconciled twice with zero mismatches.
- Cutover and rollback rehearsed with test bot tokens.

The systemd templates still default to `RedisPrimaryPostgresShadow` with all shadow flags false.
Keep those defaults until the rehearsal and cutover protocol pass.
