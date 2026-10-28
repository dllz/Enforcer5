# Detection v2: design

Approved by Daniel on 2026-10-27 after four review rounds (the questions and his answers are in
the `enforcer` memory dataset and in `docs/detection-handover.md`). Build order is section 13;
every step is a local commit, and nothing is pushed until Daniel says "push".

## 1. What Detection is

Detection is the home of **every automatic check** Enforcer runs, the existing ones and the new
ones. Today each automatic check is its own flow (`Commands/OnMessage.cs`, `UpdateHandler`)
with its own settings hash, action list and notice:

- flood
- name and text length
- Arabic/RTL in text, and in names on join
- media and link locks
- blocked inline bots and sticker sets
- channel posts
- Mute On Join
- the premium mass-add tempban (two hardcoded groups)
- the raid link reports

In v2 each is a **check** in one engine with a shared action type, a shared report, shared
recording and one menu under `/config`. Script and length checks become **filter presets**
(section 5).

### Two phases

| Phase | May use | Runs | Examples |
|---|---|---|---|
| **Inline** | `MessageContext` (the one batched read per message), in-memory caches, the policy snapshot | in the message handler, as today | flood, media locks, inline bots, sticker sets, channel posts, filters on text and names, edit rules, banned media by file id, name checks on join |
| **Enriched** | the profile queue, the mention queue, PostgreSQL, the ML service | dispatched work; the result adds to the score when it arrives | bios and channels, outside mentions, similarity to banned content, image and text classes |

- Inline checks keep today's guarantee: flood and the locks work during a raid, with PostgreSQL
  down and with every model down.
- Only the enriched phase is best effort.
- **Detection never sits in front of the enforcement queue.** Raid mode, flood control and the
  queue work exactly as today when everything new is missing.

Both phases produce a `SignHit(sign, weight, evidence)`, which goes through:

1. the policy (action),
2. the score,
3. the report,
4. the record (buffered, flushed to PostgreSQL once a minute, dropped when it is down).

### Shared actions

`none`, `score`, `report`, `delete`, `warn`, `mute`, `tempmute`, `kick`, `tempban`, `ban`.
Any action can be combined with delete and with report. All go through `Methods.*`, so through
the enforcement queue, with reason `detect:<check>` in `sanctions`.

### Who is checked

- **Newcomer** = `MemberTrust` level New: not established, not on the auth or watch lists, not
  an admin. Newcomer signs (section 3) only apply to them.
- Existing checks keep their current reach (flood, locks etc. apply to everyone except admins
  and watched members, as today).
- Edit rules apply to everyone except admins and approved members.

### Modes

Per group: `observe` (records what would have happened, no reports, no actions) or `live`.
**Every group starts with 2 weeks of observe**; `/config` says when it ends and what it found so
far ("would have reported N members"). Exceptions, live from day one:

- the ported existing checks (they already act today),
- the edit rules.

Per rule and per model, `trial` / `shadow` record only, alongside the live decision.

## 2. Settings in PostgreSQL

All settings for the automatic checks move to PostgreSQL: flood, flood exceptions, media
locks, char, length, Mute On Join, the warn settings the checks use, inline bot and sticker
set blocks, plus everything new.

- PostgreSQL is the source of truth.
- Each group's policy is one immutable snapshot, cached in memory and mirrored to Redis as JSON
  (`detect:policy:{chat}`), so inline checks work when PostgreSQL is unreachable, also after a
  restart.
- **Lazy one-way import:** the first time a group has no PostgreSQL rows, its Redis hashes are
  copied in (the import is the only code that knows `Flood = "yes"` means off). The old hashes
  are **not deleted in this work**, so a binary rollback still finds them.
- Schema change approved: `detection_policies` and `group_filters` replace the approved ERD's
  `group_flood_exceptions`, `group_character_policies`, `group_length_policies` and
  `group_media_policies`. What the checks need from `group_settings` is copied from the ERD
  unchanged.

## 3. Signs

| Sign | Phase | Default weight | Default action |
|---|---|---|---|
| `link_outside`: link to another chat or website | inline | 2 | score |
| `mention_outside`: @mention of a chat outside the group | enriched | 2 | score |
| `forward_channel`: forward from a channel or hidden sender | inline | 1 | score |
| `first_post_media` | inline | 1 | score |
| `posted_soon`: first post within 60s of joining | inline | 1 | score |
| `same_as_newcomers`: same content as another newcomer in 24h | inline | 2 | score |
| `name_impersonation`: of an admin or a bot of the group | inline | 4 | report |
| `name_keyword`: admin, support, official... | inline | 2 | score |
| `bio_link`, `bio_mention`, `personal_channel` | enriched | 1 | score |
| `channel_reputation` (section 8) | enriched | 3 | score |
| `banned_media`: same file id, or near copy by PDQ hash | inline (id) / enriched (hash) | 3 | delete |
| `similar_to_banned`: text embedding close to a banned member's message | enriched | 2 | score |
| `image_nsfw`, `image_class` | enriched | 3 | report (shadow first) |
| `text_ad`, `text_scam`, `text_toxic` | enriched | 2 | report (shadow first) |
| `joined_during_raid` | inline | 1 | score |
| `filter:<id>` | inline (text, name) / enriched (bio, channel) | rule's | rule's |
| `edit_swap` | inline | n/a | **report + delete, always** |
| `edit_late` (over 10 min after the post) | inline | n/a | newcomers: report + delete; established: report |

- **Score.** Each sign counts once per member per day; the score is kept 24h or until the
  member is established. Thresholds per group: report at 3, mute at 6 (mute off by default).
  Kept in memory; after a restart it is rebuilt for a member from `detections` when their
  next sign fires.
- **Impersonation.** Names normalised with `MessageFingerprint.Normalize` (lookalike letters
  folded; emoji, separators and zero-width characters dropped), compared with the group's
  admins (`AdminDirectory`) and bots (bot admins, bots seen joining, Enforcer itself): equal,
  edit distance ≤ 1 for short names, or an admin's name plus a keyword. On join and on an
  observed name change.
- **Banned media.** When an admin bans or kicks by replying to a media message, through report
  buttons, or with `/banmedia` as a reply, its `file_unique_id` (and PDQ hash once the ML step
  exists) is stored for the group.
- During a raid (`RaidGuard.Quiet`) detection records but sends no reports, and profile lookups
  pause for that group.

## 4. Edits

Telegram sends the original `date` with every edit, so "late" needs no record. A swap does:

- For every **media** message from a non-admin: `media:{chat}:{yyyymmdd of the post}` hash,
  field `messageId` = `type:file_unique_id`, TTL 31 days, pipelined fire-and-forget. Text
  writes nothing.
- On an edit, one `HGET`:
  - record found and type or unique id differs → `edit_swap`;
  - no record, the edit is media, the post is inside the record window → it was text →
    `edit_swap`;
  - post older than the record window → `edit_late`;
  - `edit_date - date` > 10 min → `edit_late`.
- Edits also run the newcomer checks and the filters on the new content.
- The report says what changed ("photo replaced by a sticker, 3 weeks after posting"), never the
  content. A swapped image goes to the NSFW model when AI is on.

## 5. Filters and presets

`/config` → Detection → Filters: a menu of prepared rules, like the existing menus. An admin
enables presets or adds their own regex. Each rule has a target (name, username, bio, text,
channel) and an action.

- **Presets:** Arabic script, RTL scripts, Cyrillic, CJK, Hebrew, Devanagari, mixed lookalike
  letters, zero-width characters, invite links, phone numbers, crypto wallet addresses,
  admin/support/official words, and the **length rules**: text longer than N characters, more
  than N lines, name longer than N.
- Length is a rule type, not a regex: NonBacktracking compiles `^[\s\S]{1000,}$` but refuses
  `{4000,}` (automaton over its 10,000-node limit; measured on .NET 10, 2026-10-27).
- Own regexes: `NonBacktracking | IgnoreCase | CultureInvariant`, 50 ms match timeout, pattern
  ≤ 200 characters, ≤ 50 rules per group. A pattern it cannot build (lookarounds,
  backreferences, counts too large) is refused with the reason. Matched against the raw and
  the normalised text.
- Migration: each group's `char` (Arab, Rtl) and `antinamelength`/`antitextlength` settings
  become the matching presets with the same action.

Commands (also reachable from the menu):

```
/filter add <name|username|bio|text|channel> <action> <regex>   starts in trial for 7 days
/filter try <regex> <text>
/filter test <id|regex>      names and usernames of members seen in the last 30 days,
                             bios in the in-memory profile cache (last 24h)
/filter list | live <id> | trial <id> [days] | del <id>
```

Test results go to the admin in private: number of matches, up to 10 example names. A rule in
trial records `detections` (mode `trial`); when the trial ends the admins get "make live /
extend / delete" with its hits and how many of those members were later banned.

**As built (step 5, 2026-10-28; Daniel's answers that day):**

- Arabic is the whole Arabic script (the old check knew about 30 letters); RTL is every direction
  override and isolate (U+202A-202E, U+2066-2069), and its tempban now acts. Both keep their old
  notices and reasons.
- Rules apply to everyone except admins and members on the watch or auth list. Two choices for all
  filters, in the menu: **check established members** (on by default, as before) and **allow bots
  in this group** (off by default): the text of a message sent via an inline bot that is a member
  of the group is not checked (some bots answer in the member's language); names still are; a bot
  outside the group never counts.
- Text rules see captions too, the addresses of text links and the name a message was forwarded
  from. Name rules run on every message and on each joiner.
- A preset acts as soon as an admin switches it on; an own regex starts on trial for 7 days; neither
  waits for the group's observe weeks. A filter acts itself (one action, the strongest of the live
  rules that matched) and records every rule that matched as `filter:<preset>` or `filter:<id>`.
- Not yet: `bio` and `channel` targets (step 11) and edits (step 8). A rule whose trial is over keeps
  recording only until an admin answers the end-of-trial notice (step 7) or uses `/filter`. The length rules keep their own menu and check.
- A regex with spaces is written between slashes in `/filter try` (`/filter try /free +crypto/ text`).

## 6. Reports

- **Default destination: admins' private chats**, for admins who started the bot; each admin can
  opt out in `/config`.
- **Optional admin chat:** `/config` → Reports → "Link admin chat" gives a `startgroup` deep link
  with a one-time token (10 minutes); the bot checks the person is an admin of both groups. One
  admin chat can serve several groups.
- The log channel gets a one-line entry for every action taken.
- **One report per member per 24h**; every copy is edited as signs add up (debounced 30s);
  copy ids kept as `FlagCommands` does for `@admin`.
- Buttons: **Ban, Mute, Fine, Go to message** (AI signs also **Correct / Wrong**, section 10).
  Callback data carries the source group; only its admins (`AdminDirectory`) can press. The
  first decision updates every copy. Every press is a label.
- Fine clears the member's score and exempts them from the same signs for 30 days.
- During a raid the group's reports are held and summed up in one report when it ends.
- The raid buttons, link-report buttons and `@admin` buttons also write labels from now on.

**As built (step 7, 2026-10-28):**

- `Detection/DetectionReports`. A report is the group, the member and when it was opened; its copies are
  rows of `report_copies` (kind `member`), and the first decision is written on every copy, so a press on
  a stale copy after a restart still finds it. The text is rebuilt from `detections` after a restart. It
  lists the signs that made up the member's score and the one that fired, never message text.
- Where they go: `EngineSettings.ReportTo` (`admins`, `chat`, `both`) and `AdminChatId`, in the engine's
  `detection_policies` row. `chat` without a linked chat goes to the admins. Linking sets `chat`.
  Opt-outs are `report_admin_optouts`, cached 10 minutes. The Reports screen is in /menu → Detection.
- The admin chat link is `startgroup=adminchat_<token>`, the token in Redis for 10 minutes
  (`detect:adminchat:{token}`). It is used up only by an admin of both chats, so someone else in the
  chat cannot spend it.
- A send or edit answered with 429 waits and tries again (6 tries), so a burst of reports to one admin
  arrives late rather than not at all.
- Fine: label `fine` (source `report`, reason = the signs, comma separated), score forgiven, the same
  signs neither count nor report for 30 days (`Detection/Fines`, loaded back from `member_labels`). A
  `fine` label also sets the outcome of the member's detections from the day before.
- Labels: report buttons are source `report`, raid alert and link-report buttons `raid` (the suspects
  banned when an admin presses Ban suspects are that admin's decision). Ignore on a raid alert is a
  `fine` label for each suspect, Ignore on a link report for that member. Delete on an `@admin`
  report is a `delete` label (buttons carry the member's id from now on). Mark solved writes no label:
  it says nothing about the member.
- The log channel gets one line for every action the engine takes.
- Filters can report: `report` is in the presets' action cycle, and an own rule could already have it.
- End of a trial: `Detection/FilterTrials` looks every 10 minutes for rules whose trial ended in the
  last 30 days. `group_filters.trial_notified_at` is claimed first, so with both editions in a group it
  goes out once, from an edition that is in the group. Changing the trial (Extend, `/filter trial`)
  clears it. The notice says how often the rule matched and how many of those members were banned or
  removed later, with Make live, 7 more days and Delete (copies in `report_copies`, kind `trial`).

## 7. Profiles and channels

- **Profile queue:** `getChat(user)` for bio, personal channel, birthdate visible, emoji status,
  business location. Copied from `MentionResolver` (paced, single-flight, bounded, cached) and
  sharing its pacer, 10/s bot-wide for both. Asked for on a newcomer's first post, or on join
  when a group has a bio rule set to act on join. Cached 24h in memory; bio text never written
  to disk, only flags (`account_profile_flags`).
- **Channels** come from: the bio (@name, t.me links), the personal channel, forwards
  (`forward_origin`), @mentions and t.me links in messages, and posts made as a channel.
- A bot can read a public channel's title, description, photo, linked chat, member count and
  (if Telegram returns it) pinned message, but **not its posts**. Each channel is looked up
  through the same queue at most once a day, shared by all groups. Title and description run
  through filters (target `channel`), the keyword and link checks and text embeddings; the
  photo through the image models. Only flags, vectors and the public id and username are kept.

## 8. Reputation (cross-group, on by default)

- Every detection and every ban records the channel ids and website domains its member or
  message pointed at (ids only).
- `channel_reputation` / `domain_reputation`: distinct members labelled bad and fine pointing
  at it, across how many groups, last seen.
- Bad when **≥ 2 bad members in ≥ 2 groups** point at it and few fine ones do (starting
  numbers, tuned per section 11). An admin's Fine counts against it.
- Sign `channel_reputation`: a newcomer whose bio, personal channel, forward or mention points
  at a bad channel scores at once, in any group.
- `/channelrep <@name|id>` (devs): what it is linked to, as counts.

## 9. AI (optional)

Everything that needs the GPU is off unless `MlServiceUrl` is set **and** the group turns the
model on (`/config` → Detection → AI, per model off / shadow / live, default off). Everything
else works without it. "Live" is offered for a model only once it passes the eval bar
(section 10).

### Service

- **Infinity** (`michaelfeil/infinity`) in Docker on the host with the GTX 1650 (4 GB), REST,
  reachable only from the bots' VM. Files and host steps in `deploy/ml/`.
- The host needs the NVIDIA driver and the **NVIDIA Container Toolkit** (a host package, not a
  container) so Docker can pass the GPU in. Check: `nvidia-smi`,
  `docker info | grep -i nvidia`.
- The bot talks to it through `IMlClient` (embed text, embed image, classify text, classify
  image), so moving to Triton later changes one adapter.
- `MlServiceUrl` from the environment (empty = off). `EmbeddingQueue`: bounded (5k, drops
  oldest), batches of 32 every 2s, 5s timeout, 1-minute pause after failures. Never awaited by
  message handling.

### Models (candidates; benchmarked on the card before any is enabled)

| Task | Model |
|---|---|
| Text embeddings | `intfloat/multilingual-e5-small` (384) |
| Image embeddings, zero-shot image labels | CLIP ViT-B/32 with the multilingual text side `sentence-transformers/clip-ViT-B-32-multilingual-v1` (512) |
| NSFW | a small head on the CLIP vector (e.g. LAION's CLIP-based NSFW detector), in our adapter |
| Toxic / hate speech | `textdetox/xlmr-large-toxicity-classifier` or `unitary/multilingual-toxic-xlm-roberta` |
| Ads, scams | zero-shot NLI `MoritzLaurer/mDeBERTa-v3-base-xnli-multilingual-nli-2mil7`, later a head on e5 trained on our labels |
| Re-encoded copies | PDQ perceptual hash, in the bot (CPU, ImageSharp) |

- **Input:** the thumbnail only (photos at 320 px; sticker thumbnails, which cover static,
  animated and video stickers; video and GIF thumbnails), downloaded from the self-hosted Bot
  API server and kept in memory only. **No image is ever written to disk.**
- **What is sent:** newcomers' text and media, everything during a raid, every message that
  fired a sign, every swapped edit, media of reported or banned messages, photos and
  descriptions of channels. Toxic speech: a group may turn it on for everyone except admins.
- Hate speech: report, never an automatic ban.
- **NSFW and CSAM:** a good NSFW model flags most explicit images, so explicit CSAM will usually
  be deleted with NSFW at delete + report; it cannot tell age and misses material that is not
  explicit. Together with the edit rules this is the defence (Daniel: NSFW is enough). Groups
  that allow adult content can set NSFW to report.

## 10. Evals

- `/eval <ok|ad|scam|hate|nsfw|spam>` as a reply (admins): records what the message **is**, not
  what a model said, so any model or threshold can be scored on it later. AI-sign reports also
  get **Correct / Wrong** buttons. Each group can turn eval marking off.
- `eval_cases`: group, message id, class, who, when, media type, `file_id` (not the file), the
  **message text (only for marked messages)**, and every model's output then. `eval_results`:
  per model version and case.
- New models re-download images by `file_id` in memory; if Telegram no longer has it, the case
  keeps its old scores and is skipped.
- `tools/evals/` (quasi box, read-only, no ids in exports): precision and recall per class,
  model version and threshold, confusion between classes. `/evalstats` for admins (their group
  and all groups). The bar for "live" is agreed with Daniel (starting point: precision ≥ 95%
  at the chosen threshold, ≥ 200 cases per class).

## 11. Similarity: testing and tuning

Five kinds: text near-duplicates (Jaccard over letter triples, 0.7), names (edit distance,
trigram), text embeddings (cosine), image embeddings (cosine), PDQ (Hamming).

- All thresholds in one `SimilarityTuning` class (like `RaidLimits`), overridable globally from
  config.
- **Near-miss recording:** comparisons close to but under the threshold (Jaccard ≥ 0.5, names ≥
  0.6, cosine ≥ 0.75, Hamming ≤ 24) are recorded with their score in `detections` (mode
  `observe`, no text), so thresholds can be replayed against labels later.
- **Fixtures in the repo** (our own text, no player data): lookalike names (Cyrillic а, Greek ο,
  zero-width joiners, emoji padding, "Name | Support", "Name_admin") and names that must not
  match; reworded texts, random endings, spacing tricks, one scam in 5 languages, look-alike
  but harmless texts. Each says must / must not match.
- Recorded raids through `RaidReplay`: still caught, regulars still clean.
- `tools/tuning/`: precision/recall per threshold, sign and group size; proposes thresholds with
  the curve. Changed thresholds and models run in shadow first.
- `/simtest <a> | <b>` (devs), `/detectstats` (admins), `/detectreview` (devs).

## 12. Data

New PostgreSQL tables (approved). Every row links to `groups.group_id` and
`telegram_accounts.account_id`; `account_id` is nullable so a row is anonymised in place.

| Table | Holds |
|---|---|
| `detection_policies` | per group and check: action, weight, enabled, thresholds; group row: mode, observe start, report destination, admin chat, AI settings, retention |
| `group_filters` | presets and own rules: target, pattern or length rule, action, mode, trial until, created by, hits |
| `report_admin_optouts`, `report_copies` | admins who opted out; ids of sent report copies |
| `detections` | sign, phase, weight, score after, action, mode, message id, similarity value, evidence `jsonb` (ids only), channel ids, domains, outcome, decided by, when |
| `member_signals` | features at join, first post, score changes and labelling: account id range, username present, premium, language, script, name features, profile flags, join source, link name, adder, first post type and delay, links, mentions, edits, activity hours, raid context |
| `member_labels` | bad or fine, source (button, sanction, global ban, established, raid button), who, when |
| `account_profile_flags` | bio and profile flags, personal channel id, checked at |
| `channel_profiles` | channel id, username, flags, member count, checked at |
| `channel_reputation`, `domain_reputation` | bad and fine member counts, groups, last seen |
| `banned_media` | `file_unique_id`, type, PDQ hash, added by, reason |
| `message_embeddings`, `media_vectors` | `halfvec(384)` / `halfvec(512)`, model, message id; HNSW cosine |
| `eval_cases`, `eval_results` | section 10 |

- **Retention is a group setting** (`/config` → Detection → Data): 90 days, 1 year (default) or
  2 years. At the end the account id is removed; features and labels stay anonymous. Vectors
  are deleted then, except those of members labelled bad, which stay anonymous as the reference
  set for up to 2 years. Eval cases follow the group's setting. Runs in the existing 03:00 job.
- Never stored: message text (except eval cases), bio text, images.
- pgvector (`halfvec`; it has no int8 type) is enabled at deploy with `CREATE EXTENSION vector`
  by a superuser; tests that need it skip without it.
- Labels: explicit (buttons) and implicit, nightly, for rows older than 30 days: bad = ban,
  kick or mute in `sanctions` within 30 days, or a global ban (automatic bans kept apart as
  source `auto`); fine = established with no sanction, or an action an admin undid.
- `/about` gets a privacy line saying what is kept and for how long.

## 13. Build order

Each step: builds in Debug and Premium, tests pass (PostgreSQL tests on subprod), harness
scenarios for what it adds, its new strings in all 11 language files (the branch's earlier ~40
keys are not translated), local commit. Chaos run (500 seeds) before the push.

1. Characterisation scenarios for every existing automatic check (no code change).
2. Settings in PostgreSQL: tables, policy snapshot cache with Redis mirror, lazy import.
3. Engine core: check, phase, actions, policy, score, `detections`, observe mode.
4. Port the existing checks behind the engine, with parity (step 1's scenarios unchanged).
5. Filter presets and length rules (char and length become presets); `/filter`.
6. Detection in `/menu` (observe status), retention, `/privacy`. **As built (2026-10-28, Daniel):** no
   `/config`; everything named `/config` in this document lives in `/menu`. `/menu` opens from the
   group (in private; an admin who never started the bot gets a one-tap deep link `menu_{chat}`)
   or in private through Telegram's group picker (`KeyboardButtonRequestChat`). Buttons use
   Telegram's styles (primary to go on, green for on, red for removing people), a setting's name
   toggles it as its value does, Done deletes the menu. `/dashboard` is removed. The privacy text
   is `/privacy`, since `/about` is each group's own text. Retention is saved now and carried out by
   step 14.
7. Reports: private chats, admin chat link, buttons, labels from every button. **As built** in section 6;
   also the end-of-trial notice of section 5.
7b. **Learning the levels from the admins** (Daniel, 2026-10-28; not in the approved design, added after
   step 7). `Detection/TrustLearning`, `TrustOverrides`, /untrust and /trust, /menu → Detection → Learned
   from you. Every change is applied at once and told to the admins with Undo and Keep (proposed only while
   the group watches), kept in `detection_adjustments`; learning can be turned off per group (on by default).
   - **Established level.** Raised when an admin bans, kicks or mutes an established member after a sign in
     the day before (any sign but flood, media lock and the length checks), or sets one back to new: halfway
     towards that member's messages (+1) and days, at most 3× the usual messages (≤ 1000) and 2× the usual
     days (≤ 60). A member already at 3× the usual messages looks like a taken-over account: no change, and
     the admins are told why. The admins are not asked what a ban was about (Daniel: "for now we do not
     ask"); the classifiers of steps 15-17 add signs, and so reasons, later. A week after the last raise it
     eases back a quarter of the way each night until it is the usual one again. The level is the higher of
     the usual (nightly norm) and the learned one, read from memory (`MemberTrust.Thresholds`).
   - **Report level.** Nightly, from 30 days of admins' decisions (ban, kick, mute, Fine, an undone decision
     counts as fine) about members with a score: the lowest score from which ≥ 70% of at least 5 were
     removed or muted; one step a night, 2 to 8, at least 10 decisions. The mute level is not learned.
   - **Set back to new** (`member_trust_overrides`): new whatever the counts, until an admin restores them
     (Daniel). After 30 days, once they wrote as many messages as the group's established level since,
     with no sign, warn or action against them, the admins are asked once (again 30 days after Keep as new).
   - Nightly work runs each hour from 03:00 UTC; with both editions in a group, whichever claims the day
     (`LearnedOn`) does it, and only an edition that is in the group.
   - **7c, abuse or the group's own rules** (Daniel, 2026-10-28: a player can be trusted to post safely
     without being trusted to keep a game's rules). Every sign has a kind (`SignCatalog`, `SignCategory`):
     *abuse* (newcomer signs, impersonation, banned media, edits, raid links, mass adds, and the presets
     lookalikes, invisible characters, invite links, phone numbers, crypto wallets, staff words) or *group*
     (flood, media lock, the length checks, inline bots, sticker packs, channel posts, the script presets
     Arabic, RTL, Cyrillic, CJK, Hebrew, Devanagari, and an admin's own `/filter` rule unless it is marked
     with `/filter add spam …` or `/filter spam <id>`). Later: hate and toxic speech are group rules. Stored
     on `detections.category`, as the group's settings said then. Every decision gets one
     (`member_labels.category`): abuse when the admin's reason names spam, ads, scams, porn or raids
     (`AbuseWords`, 9 languages; only the kind is kept), else from the signs of the day before (abuse wins),
     a warn being a group rule, null when nothing is known; a sign written after the decision updates it.
     Only abuse raises the established level, counts for the report level and keeps a member set back to
     new from being offered back. `/user` shows both records for the last 90 days.
   - The number of members who become new on a raise is not shown: message counts before the PostgreSQL
     rollout (25 Oct) are only in Redis, so it cannot be counted cheaply.
8. Edit rules. **As built (2026-10-28):** `Detection/EditCheck`. Media is recorded for everyone but admins
   the bot knows without a lookup; `media:since:{chat}` marks when recording began, so text turned into
   media is only called a swap for posts made since (an older post might have been media not recorded).
   Edits of posts of any age are checked (the 2-day limit stays for raid detection only), and every
   edit goes through the filters. `edit_late` by default deletes for new members and reports for
   everyone; /menu → Detection → Edits turns late edits off, the deleting, or the reporting of
   established members (`SignPolicy.DeleteNewcomers`, `SkipEstablished`). The report line says what
   changed and how long after posting; after a restart a report rebuilt from `detections` loses that
   detail. Both signs are abuse.
9. Message signs. **As built (2026-10-28):** `Detection/MessageSigns`. Inline on the moderation path (no
   lookup): links (URL and hidden-link entities) not to this group, forwards from a channel or a hidden
   sender, @mentions, content worth comparing; the rest is dispatched work, for new members only (one
   trust lookup): `link_outside`, `forward_channel` (not the group's linked channel), `mention_outside`
   (through `MentionResolver`), `same_as_newcomers` (same file, same text hash, or alike texts of 20+
   letters from another new member in 24h, kept in memory, 500 a group; both members get the sign).
   `first_post_media` and `posted_soon` (60s after `joined`) come from the stats' message count, so they
   cost one read on a first message only. `joined_during_raid` on joins while `RaidGuard.Quiet`. Edits run
   them too. No new strings: the sign names were added in step 7.
10. Name signs. **As built (2026-10-28):** `Detection/NameSigns`. Names (display name and username, either way
    round) folded by `MessageFingerprint.Normalize` and compared with the group's admins (`AdminDirectory`) and
    bots (bots among the admins, bots seen joining, kept in memory, 20 a group, and this bot): the same (3+
    letters), one letter apart counting a swap as one (5 to 12 letters; two for longer names), or the admin's
    name with only staff words added ("Daniel | Support", "daniel_admin"). `name_keyword` is the staff words
    preset's test (admin, support, official, moderator, helpdesk, customer care). Checked on join (service
    message and member update; the admin list is looked up) and on every message and edit against the list at
    hand (a group without one gets one lookup every 10 minutes), so a rename shows on the next message. A name
    that matched is remembered a day per member, so it is recorded once. Both signs are for new members only,
    as designed. The report line names whom the name copies. No new strings.
11. Profile and channel queue.
12. Banned media.
13. Similarity tuning, fixtures, near-miss recording, `/simtest`, `tools/tuning/`.
14. `member_signals`, implicit labels, reputation, retention, `/detectstats`, `/detectreview`,
    `/channelrep`.
15. ML service, `IMlClient`, queue, vector tables, PDQ, `similar_to_banned`, AI settings.
16. Evals.
17. Image and text classification signs, shadow; live per model after the eval bar.
18. v2 improvements to ported checks, one commit each: flood with the shared actions, newcomer
    hold (was Mute On Join), mass-add for any group, media action per type.
19. Training script and shadow model.
