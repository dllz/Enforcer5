# Handover: member detection, labels and pattern learning

**Superseded by `docs/detection-design.md`** (approved 2026-10-27), which is the design that is
being built. This file is kept as the record of what was asked for.

Written 2026-10-27 at the end of the raid-readiness work, for a fresh session to design the next
stage. Nothing below is built yet. Raid mode itself is done; see `docs/raid-runbook.md` and the
"Rate limits, enforcement and raids" section of CLAUDE.md.

## State of the branch

`Migration` has about 35 local commits on top of `origin/Migration` (check
`git log origin/Migration..HEAD`). They hold the raid work: the enforcement queue, the slow lane,
raid detection and raid mode, raid telemetry, readiness fixes, the mention lookup queue, and the
API caches (`AdminDirectory`, `ChatDetails`, `GroupSettings`, quiet mode, debounced saves).
Verified: 698 tests in both builds, the PostgreSQL tests on subprod, and 500 chaos seeds. Whether
to push them before the new work starts is Daniel's call; ask.

Strings: upload #191 (English, the 8 `trust...` keys) is still "unverified" because
`/languages/enforcer` is empty on the quasi host. Daniel sent the live Enforcer language files as
`/workspace/inbox/20261026-160024-languages.zip` (11 files: English, Deutsch, Russion, Persian,
Portuguese, Bahasa_Indonesia, Cantonese, Mandarin, Turkish, Italian, Spanish; unpacked in
`/workspace/scratch/enf-lang-zip/`). **We maintain all of them** (Daniel, 2026-10-27). They are
behind: English there has 445 keys against 485 in the repo, the others 124 to 319, and none has the
raid, trust or slow-lane strings. Still open: whether to translate this branch's existing ~40 new
keys into the other 10 files.

## What Daniel asked for

1. **Everyday detection, not only raids**, so it is validated before a raid depends on it. It
   covers new and suspected members in general, every check configurable per group (action and
   thresholds, from a dashboard page):
   - links, and @mentions pointing outside the group (`Raids/MentionResolver` already tells inside
     from outside, through a paced, cached queue)
   - forwards from channels, a first post that is an image, posting soon after joining, the same
     content as other newcomers
   - name checks: impersonating any **admin** or any **bot** in the group (normalised, so lookalike
     letters don't help), words like admin/support/official
   - bio and personal channel ads (`getChat(user)`, through a queue like the mention one, cached)
   - **no profile photo checks for now** (Daniel)
   - a banned-media list: the file unique id of anything an admin banned for is deleted on repost.
     The bot cannot see what an image shows; only ids, sizes and captions.
2. **Edits**, a known attack: old posts edited into something against Telegram's terms (copyright,
   CSAM) that other accounts then report, to get the group deleted. Daniel's rules:
   - text edited into an image or sticker, an image or sticker swapped, any change of type:
     **report and delete, always** (everyone but admins and approved members)
   - any edit beyond the first few minutes of a post, especially by members who are not
     established: report and delete for new members, report for established (adjustable)
   - this needs a record of what each media message was (id, type, file unique id; never text),
     e.g. 30 days in Redis. `edited_message` is already received (`UpdateHandler.RouteAsync`).
3. **Admin regex rules** (`/filter`, since `/rules` is taken) on name, username, bio or text, with
   an action. They must be testable against real data: `/filter test` against the names of recent
   joiners and members (already in PostgreSQL) and cached bios; `/filter try <regex> <text>`; and a
   trial period where a rule only reports what it would have done. Regexes run with
   `RegexOptions.NonBacktracking` and a match timeout.
4. **Suspicion score and "report suspicious activity"** (configurable): one score per member from
   all signs, one report rather than one per sign.
5. **Store member data and learn from human decisions.** Daniel is happy to store more, as long as
   no message text is stored:
   - `detections`: every sign that fired, the action, and its outcome (banned, ignored, undone,
     none), who decided, when
   - `member_signals`: features at join and when the score changes. Account (id range as account
     age, username, premium, language_code), name features, bio flags (never the text), join
     source and invite link, first post type and timing, links, mentions, edits, activity hours (a
     time-zone proxy), raid context, and profile extras from getChat (birthdate if visible, emoji
     status, personal channel, business location). **Telegram does not give bots an account's
     location or phone**; language, script and activity hours are the proxies.
   - labels: explicit (Ban/Ignore buttons) and implicit (banned, kicked or muted within 30 days,
     global ban; or established with no action, as "fine")
   - retention proposed and not yet confirmed: unlabelled 90 days, labelled 1 year, then the
     Telegram id is dropped and features and label stay, anonymous. Detections must be kept longer
     than the 90 days first proposed, because they are the training data.
   - learning in stages: per-sign precision first (`/raidstats` per group, `/raidreview` for devs),
     then a model trained offline on the quasi box (logistic regression first, readable weights)
     shipped as config, run in shadow until it beats the hand-set score on held-out recent data.
     `FindSimilar` (resemblance to known raiders, already built) becomes a feature.
   - a privacy line in the bot's /about or /privacy text was proposed, not yet confirmed.
6. **Message embeddings** as a feature: similar spam across rewording and languages, campaign
   clusters, closeness to messages that got someone banned.
   - Daniel's GTX 1650 (4 GB) is on the server the bots run on, **not passed through to the bots'
     VM**: run the model in a **Docker container on the server, reached over REST** (e.g. Hugging
     Face text-embeddings-inference, which supports Turing; `multilingual-e5-small` or
     `paraphrase-multilingual-MiniLM-L12-v2`, about 120M parameters).
   - the bot sends asynchronously through a queue and never waits on it; if it is down, only the
     vectors are missing
   - embed only non-established members' messages, everything during a raid, and detected messages
   - PostgreSQL `pgvector` 0.8.6 is installed on the server (checked 2026-10-27), but not yet
     enabled in `enforcer` or `enforcer_subprod`: `pg_extension` there lists no `vector`. It is
     not a trusted extension, so the bot's migrations cannot enable it; Daniel runs
     `CREATE EXTENSION vector;` as superuser in both databases before the first migration that
     uses it. pgvector has no int8 type: `halfvec` (see the design)
   - vectors are personal data (text can be roughly reconstructed from them): same retention,
     dropped on anonymisation

## Constraints to respect

- Raid mode must keep working without any of this: detection data, the profile queue and the
  embedding service are best effort and never on the enforcement path.
- Telegram API pressure: everything per member goes through queues with caches (see the caches
  listed in CLAUDE.md); nothing new per message during a raid without a cache.
- PostgreSQL tables: the approved schema is on `feature/postgres-migration`
  (`docs/storage-erd.md`); these tables are new design and need Daniel's OK.
- Tests: harness scenarios for every check (`tests/Enforcer5.Tests/Harness/`), the chaos run
  before pushing anything touching `Raids/`, `Enforcement/` or the gateway.
- Strings: every new key in all 11 language files, uploaded before the push (AGENTS.md).

## Suggested first step for the new session

Recall the `enforcer` memory dataset, read this file, CLAUDE.md and `docs/raid-runbook.md`, then
propose a design to Daniel covering: the data model (tables, retention, anonymisation), the
signals and their per-group settings, the score and reports, the edit record, `/filter`, the
label pipeline, the embedding service's API and deployment, and a staged plan with the order of
commits. Open questions to carry: retention numbers, privacy line, translating the existing ~40
keys, enabling pgvector in both databases (see 6), and whether the current local commits are pushed first.
