# Raid runbook

What raid mode does, what group admins can do, and what to check when it goes wrong. The code is in
`src/Enforcer5/Raids/`; how it is tested is in CLAUDE.md (Rate limits, enforcement and raids; the
group-chat harness).

## What happens in a raid

Each group keeps a minute of signs. Only untrusted members count (not admins, not people on the
group's `auth` or `watch` lists, not established members; `/user` shows anyone's level):

| Sign | Score | Harmful |
|---|---|---|
| A wave of joins (15 a minute, or 20 times the group's usual; approved join requests do not count) | 2 | no |
| 3+ newcomers posting the same link, file or long text (near copies and lookalike letters count) | 3, 4 with 6+ | yes |
| 3+ newcomers posting the same short plain text ("echo", e.g. a giveaway) | 1 | no |
| 3+ newcomers flooding | 2 | yes |
| 3+ newcomers posting nothing but a link | 2 | yes |
| 3+ joiners posting within 30s of joining | 1 | no |
| 5+ @admin reports | 1 | no |

A score of 2 is a possible raid, 4 a raid. Edited messages are checked too, and channels posting
in the group are treated like newcomers (the group's linked channel never is).

An @mention counts as a link only when it points outside the group: a channel, group or bot
elsewhere, someone who is not a member, or a name nobody knows. A member, the group itself, its
linked channel and @admin are conversation. A newcomer's mention is looked up through one queue for
the whole bot (Redis first: `bot:usernames` and the member's `{chat}:users:{id}`; then Telegram, at
most 10 lookups a second, pausing after a 429), and the answer is kept: outside for 6 hours, inside
for an hour, a failed lookup (counted as outside) for a minute. During a raid the advertised name is
looked up once; every later message with it is checked from memory.

**Human mode** (default): the bot tags every admin with *Possible raid* and four buttons:

- **Lock the group**: raid mode with everything: only admins can post, everyone joining is muted
  for 24 hours, every suspect is banned with their messages.
- **Mute new joiners**: only the 24-hour mute for joiners.
- **Ban the spammers**: joiners muted and suspects banned, the group stays open.
- **Ignore**: nothing is done; the suspects are let off for a day, and no new alert comes for half
  an hour unless it is clearly a raid.

**Auto mode**: the bot acts at once and tells the admins. Short of a raid, or without harm, it holds
back: joiners muted, harmful suspects muted and their spam deleted. A raid with harm gets
everything Lock does. A surge without harm never locks the group or bans anyone.

**Off**: no detection and no link reports. `/raid` still works.

Separately, a newcomer posting nothing but a link is reported to the admins with **Ban** and
**Ignore** (in human and auto mode).

Raid mode ends by itself after 10 minutes without signs or a stream of joins (3 a minute), with
`/raid off`, or with the notice's **End raid mode** button. The group's own permissions are put
back exactly as they were before the lock. The summary has **Unmute the new joiners now**, which
unmutes exactly the people this raid muted.

## Commands (group admins)

- `/raid`: raid mode on now, with everything (lock, mute joiners, ban suspects).
- `/raid off`: end it; the group's permissions come back.
- `/raidmode human|auto|off`, or `/raidmode` to see the current mode.

## When something goes wrong

**The group is still locked after the raid.** First `/raid off` in the group. If the bot says raid
mode is not on, the unlock may still be waiting in the enforcement queue (a 429 or missing rights):
check `enforcement:{normal|premium}:jobs` for a `SetChatPermissions` job for that chat
(`enf-redis HSCAN enforcement:normal:jobs 0 MATCH '*<chat id>*'`). If nothing is waiting, an admin
sets the permissions back by hand in the group's settings (Permissions).

**Raid mode does not end.** Something keeps it going: the notice's counts show joins or bans
still coming. Read `raid:{edition}:{chat}` (`LastActivityUtc`, `Reasons`). `/raid off` always
ends it.

**The bot does nothing.** It needs to be an admin with the rights to ban, restrict, delete and
change the group's permissions; without them it says so in the group once per 10 minutes, and the
jobs wait 10 minutes for the rights to come back. Check `enf logs <config> app` for 429s.

**The admins say they never got the alert.** It waits out 429s for up to half an hour; check the
log for `RaidAlert`. In a group that hides its admins, tags may not reach everyone.

**Too many false alarms in a group.** `/raidmode human` if it is on auto, or `/raidmode off`.
`/raidprofile` (devs) sends the incident's profile as JSON; replay it in the harness
(`RaidReplay`) to tune the limits in `RaidLimits`.

## Redis keys

| Key | What |
|---|---|
| `raid:{edition}:{chat}` | The incident in progress (JSON): phase, actions, saved permissions, suspects |
| `raids:{edition}` | Chats with an incident in progress, reloaded at startup |
| `raidq:{edition}:{chat}:{incident}` | Who the raid muted, for the release button (expires after the mute) |
| `chat:{chat}:raid` | The group's raid mode (`mode`) |
| `enforcement:{edition}:jobs` | Every ban, mute, lock and unlock not yet carried out |

## Dry run before a release

With a test bot token, a scratch Redis DB and a test supergroup with a few test accounts (never
production Redis: two consumers fight over the update offset):

1. `/raidmode human`. From 3 accounts that have not talked there, post the same link within a
   minute: the alert appears with the admins tagged. **Ignore**: nothing happens, no new alert.
2. Again from 3 others, then **Lock the group**: the group is locked, the 3 are banned and their
   posts gone, a new account joining is muted.
3. `/raid off`: the group's permissions are exactly as before (set a custom one, e.g. no photos,
   beforehand). **Unmute the new joiners now** unmutes the joiner and nobody else.
4. `/raidmode auto`. From 3 new accounts, post the same sentence with a link in it (not a bare
   link): muted and deleted at once, the group open. From 6, or with bare links: banned and the
   group locked.
5. Post a link as an admin, and reply `@admin` from a new account: neither is reported as a link.
6. Edit a new account's message into a bare link: it is reported.
