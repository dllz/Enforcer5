using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Data;
using Enforcer5.Enforcement;
using Enforcer5.Helpers;
using Enforcer5.Models;
using StackExchange.Redis;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5.Raids
{
    /// <summary>
    /// Raid detection and raid mode.
    /// <para>
    /// <b>Detecting.</b> Each group keeps a minute of signs (<see cref="RaidSigns"/>): joins, and from
    /// untrusted members only (<see cref="MemberTrust"/>; admins, the group's approved lists and
    /// established members never count) the same text or image from several of them (near copies and
    /// lookalike letters included), floods, bare links, posting the moment they joined; and a burst
    /// of @admin reports. They add up to a score. Joins an admin let in (approved requests) are not
    /// counted; channels posting in the group are members like any other; edits are checked too.
    /// </para>
    /// <para>
    /// <b>Acting</b>, by the group's <see cref="RaidMode"/>: in human mode (the default) a possible raid
    /// tags the admins and asks what to do (lock the group, mute new joiners, ban the suspects, or
    /// ignore); in auto mode raid mode goes on at once and the admins are told, but only harm (spam
    /// posted alike, floods, bare links) closes the group or touches people: a surge of joins alone
    /// only holds new joiners back, and suspects are muted with their spam deleted until it is clearly
    /// a raid, then banned. <c>/raid</c> turns it on in any mode. Raid mode can lock the group (only admins can post; its own permissions are
    /// saved and put back), mutes everyone who joins for a day, and bans untrusted members showing
    /// raid signs, deleting their messages in the same call. All of it goes through the enforcement
    /// queue, so none of it is lost to a 429; the alert and notices wait out a 429 rather than being
    /// dropped. It ends after <see cref="RaidLimits.QuietEnd"/> without signs or a stream of joins, or
    /// with <c>/raid off</c> or its button; if the raid keeps coming, so does raid mode. Buttons carry
    /// their incident, so one from an earlier raid does nothing.
    /// </para>
    /// <para>
    /// Separately, an untrusted member posting nothing but a link is reported to the admins with Ban and
    /// Ignore buttons, whatever the mode (unless off); nothing is deleted until an admin decides.
    /// </para>
    /// </summary>
    internal static class RaidGuard
    {
        internal static RaidLimits Limits { get; set; } = new RaidLimits();

        private static readonly object Lock = new();
        private static readonly Dictionary<long, RaidSigns> Signs = new();
        private static readonly Dictionary<long, RaidIncident> Incidents = new();
        // Per incident id, while its lock is being put in: true once the lock job is in the queue,
        // false when the raid ended before it got there and no lock was asked for. The unlock waits
        // for it, so it can never be queued ahead of the lock and leave the group locked.
        private static readonly Dictionary<string, TaskCompletionSource<bool>> Locking = new();
        private static readonly Dictionary<(long, long), DateTime> LinkReported = new();
        private static readonly Dictionary<(long, long), (TrustLevel Level, DateTime At)> TrustSeen = new();
        private static readonly Dictionary<long, (RaidMode Mode, DateTime At)> Modes = new();
        private static readonly TimeSpan TrustFor = TimeSpan.FromMinutes(5);
        // Members an admin let off (an ignored link report or alert): no suspects again until then.
        private static readonly Dictionary<(long, long), DateTime> Cleared = new();
        // An admin ignored an alert: no new one until then, unless it is clearly a raid.
        private static readonly Dictionary<long, DateTime> IgnoredUntil = new();
        private static readonly TimeSpan IgnoreFor = TimeSpan.FromMinutes(30);
        // The current incident's spam as text sketches, so a near copy is known spam however late.
        private static readonly Dictionary<long, List<(HashSet<int> Sketch, bool Echo)>> SpamSketches = new();
        private static int _hooked;

#if PREMIUM
        private const string Edition = "premium";
#else
        private const string Edition = "normal";
#endif
        internal static string IncidentKey(long chatId) => $"raid:{Edition}:{chatId}";
        internal static string ActiveSetKey => $"raids:{Edition}";
        internal static string SettingsKey(long chatId) => $"chat:{chatId}:raid";

        /// <summary>Hooks into joins and floods and starts the loop that ends quiet raids.</summary>
        internal static void Start(CancellationToken stop)
        {
            Hook();
            Bot.StartService(async ct =>
            {
                await LoadAsync();
                while (!ct.IsCancellationRequested)
                {
                    await Clock.WaitAsync(TimeSpan.FromSeconds(15), ct);
                    if (ct.IsCancellationRequested) return;
                    try { Tick(); }
                    catch (Exception e) { ErrorReporter.Report(e, "Raid check"); }
                }
            }, stop, "RaidGuard");
        }

        private static void Hook()
        {
            if (Interlocked.Exchange(ref _hooked, 1) != 0) return;
            MemberEvents.Joined += OnJoin;
            MemberEvents.Vetted += OnVetted;
            FloodTracker.Flooded += (chat, user) => Bot.Dispatch(() => OnFloodAsync(chat, user), "RaidFlood");
            RaidCapture.RaidOn = IsActive;
        }

        /// <summary>True while raid mode is on in the chat.</summary>
        internal static bool IsActive(long chatId)
        {
            lock (Lock) return Incidents.TryGetValue(chatId, out var i) && i.Phase == RaidPhase.Active;
        }

        /// <summary>
        /// True while there is a raid alert or raid mode in the chat: per-member notices, link reports,
        /// the welcome and the log channel's single lines keep quiet or are gathered up.
        /// </summary>
        internal static bool Quiet(long chatId)
        {
            lock (Lock) return Incidents.ContainsKey(chatId);
        }

        /// <summary>The incident in a chat, a copy for tests and /raid; null when there is none.</summary>
        internal static RaidIncident IncidentIn(long chatId)
        {
            lock (Lock) return Incidents.TryGetValue(chatId, out var i) ? RaidIncident.FromJson(i.ToJson()) : null;
        }

        // ---------------------------------------------------------------- settings

        internal static RaidMode ModeOf(long chatId)
        {
            var now = Clock.UtcNow;
            lock (Lock)
            {
                if (Modes.TryGetValue(chatId, out var m) && now - m.At < TimeSpan.FromMinutes(1)) return m.Mode;
            }
            RaidMode mode;
            try
            {
                var value = Redis.db.HashGetAsync(SettingsKey(chatId), "mode").GetAwaiter().GetResult();
                mode = value.HasValue && Enum.TryParse<RaidMode>(value.ToString(), true, out var parsed) ? parsed : RaidMode.Human;
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Reading the raid mode of {chatId}");
                mode = RaidMode.Human;
            }
            lock (Lock) Modes[chatId] = (mode, now);
            return mode;
        }

        internal static void SetMode(long chatId, RaidMode mode)
        {
            Redis.db.HashSetAsync(SettingsKey(chatId), "mode", mode.ToString().ToLowerInvariant()).GetAwaiter().GetResult();
            lock (Lock) Modes[chatId] = (mode, Clock.UtcNow);
        }

        // ---------------------------------------------------------------- signs

        private static RaidSigns SignsFor(long chatId)
        {
            if (!Signs.TryGetValue(chatId, out var s)) Signs[chatId] = s = new RaidSigns();
            s.Trim(Clock.UtcNow, Limits.Window);
            return s;
        }

        private static void OnJoin(JoinEvent join)
        {
            try
            {
                var chatId = join.ChatId;
                bool quarantine;
                var vetted = join.Source == "request" || join.AddedBy is { } adder && IsKnownAdmin(chatId, adder);
                lock (Lock)
                {
                    var signs = SignsFor(chatId);
                    // An admin let them in: no sign of anything, and nobody to hold back.
                    if (!vetted) signs.Joined(join.UserId, Clock.UtcNow);
                    Incidents.TryGetValue(chatId, out var incident);
                    if (incident != null)
                    {
                        incident.Joined++;
                        incident.Joiners.Add(join.UserId);
                        // Only a stream of joins keeps raid mode going; a member joining now and then does not.
                        if (!vetted && signs.JoinsKeepItGoing(Limits, MemberTrust.JoinsPerHour(chatId)))
                            incident.LastActivityUtc = Clock.UtcNow;
                    }
                    quarantine = !vetted && incident is { Phase: RaidPhase.Active } && incident.Actions.HasFlag(RaidActions.Quarantine) &&
                                 incident.Quarantined.Add(join.UserId);
                    if (quarantine) incident.Muted++;
                }
                RaidTelemetry.Joined(join);
                if (quarantine) Quarantine(chatId, join.UserId);
                if (!vetted && (ModeOf(chatId) != RaidMode.Off || IsActive(chatId))) Evaluate(chatId);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Raid detection on a join");
            }
        }

        /// <summary>
        /// A join first seen without detail turned out to be an approved join request: it is taken back
        /// out of the signs, and out of the quarantine if raid mode had muted them.
        /// </summary>
        private static void OnVetted(JoinEvent join)
        {
            try
            {
                bool release;
                lock (Lock)
                {
                    SignsFor(join.ChatId).Vetted(join.UserId);
                    release = Incidents.TryGetValue(join.ChatId, out var incident) && incident.Quarantined.Remove(join.UserId);
                    if (release) incident.Muted--;
                }
                if (release)
                    Bot.Dispatch(async () => await EnforcementQueue.SubmitAsync(EnforcementJob.Unmute(join.ChatId, join.UserId).For("Approved join request", null)),
                        "RaidVetted");
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Raid detection on an approved join");
            }
        }

        /// <summary>
        /// Every group message on the moderation path, and every edit (<paramref name="edited"/>), so a
        /// post edited into spam later is seen too. Cheap unless the message looks like a sign: the
        /// trust lookup (a few Redis reads) only happens for bare links, content several people
        /// posted, and joiners posting at once.
        /// <para>
        /// An @mention from an untrusted member is a link only when it points outside the group
        /// (<see cref="MentionResolver"/>). When that is not known yet, the message is looked at again
        /// once the lookup queue has answered (<paramref name="mentionsLookedUp"/>, as of <paramref name="at"/>).
        /// </para>
        /// </summary>
        internal static async Task OnMessageAsync(MessageContext ctx, bool edited = false, DateTime? at = null, bool mentionsLookedUp = false)
        {
            try
            {
                var message = ctx.Message;
                if (!ctx.IsGroup || message.NewChatMembers != null || message.LeftChatMember != null) return;
                if (SenderOf(ctx) is not { } user) return;
                var chatId = ctx.ChatId;
                var active = IsActive(chatId);
                if (!active && ModeOf(chatId) == RaidMode.Off) return;
                var now = at ?? Clock.UtcNow;
                Func<MessageEntity, bool?> mentionOutside = null;
                var mentions = MentionResolver.MentionsIn(message);
                if (mentions.Count > 0 && await TrustOfAsync(chatId, user) == TrustLevel.New)
                {
                    var chatUsername = message.Chat.Username;
                    var unknown = mentions.Where(m => MentionResolver.Known(chatId, m, chatUsername) == null).ToList();
                    if (unknown.Count > 0 && !mentionsLookedUp)
                    {
                        var lookups = unknown.Select(m => MentionResolver.ResolveAsync(chatId, m, chatUsername)).ToList();
                        Bot.Dispatch(async () =>
                        {
                            // Parked: it only waits for the lookup queue, which is dispatched work of its own.
                            Clock.Park();
                            try { await Task.WhenAll(lookups); }
                            finally { Clock.Unpark(); }
                            await OnMessageAsync(ctx, edited, now, mentionsLookedUp: true);
                        }, "RaidMentions");
                        return;
                    }
                    var text = message.Text ?? message.Caption ?? "";
                    mentionOutside = e =>
                    {
                        var mention = e.Type == MessageEntityType.TextMention && e.User != null
                            ? new Mention(null, e.User.Id)
                            : e.Offset >= 0 && e.Offset + e.Length <= text.Length && e.Length > 1
                                ? new Mention(text.Substring(e.Offset + 1, e.Length - 1).ToLowerInvariant(), null)
                                : null;
                        // Still not known (the queue was full): outside, as advertising is.
                        return mention == null || MentionResolver.Known(chatId, mention, chatUsername) != MentionPlace.Inside;
                    };
                }
                var print = MessageFingerprint.Of(message, mentionOutside);
                var key = ContentKey(message, print);
                var harmful = key != null && IsHarmfulContent(message, print);
                var sketch = key != null && print.MediaUniqueId == null ? MessageFingerprint.Sketch(message.Text ?? message.Caption) : null;

                IReadOnlyList<RaidSigns.Posting> same = Array.Empty<RaidSigns.Posting>();
                DateTime? joinedAt;
                var known = KnownContent.None;
                lock (Lock)
                {
                    var signs = SignsFor(chatId);
                    if (key != null) (key, same) = signs.Posted(key, sketch, user, message.MessageId, now, Limits.SimilarText);
                    joinedAt = signs.JoinedAt(user, now);
                    if (key != null) known = Known(chatId, key, sketch);
                }
                var posters = same.Select(p => p.User).Distinct().ToList();
                var postedSoon = !edited && joinedAt.HasValue && now - joinedAt.Value <= Limits.PostSoonAfterJoin;
                var sameContent = posters.Count >= Limits.SameContentUsers;
                var knownSpam = known != KnownContent.None;
                if (!sameContent && !print.BareLink && !postedSoon && !knownSpam)
                {
                    RaidTelemetry.Message(chatId, user, print, null);
                    return;
                }

                // Who among them is untrusted.
                var untrusted = new List<long>();
                foreach (var poster in (sameContent ? posters : new List<long> { user }).Take(50))
                    if (await TrustOfAsync(chatId, poster) == TrustLevel.New) untrusted.Add(poster);
                var senderUntrusted = untrusted.Contains(user);
                RaidTelemetry.Message(chatId, user, print, senderUntrusted);

                var newSuspects = new Dictionary<long, List<int>>();
                var echoers = new Dictionary<long, List<int>>();
                void Suspect(long who, Dictionary<long, List<int>> into = null)
                {
                    into ??= newSuspects;
                    if (!into.TryGetValue(who, out var ids)) into[who] = ids = new List<int>();
                    ids.AddRange(same.Where(p => p.User == who).Select(p => p.MessageId));
                    if (who == user && !ids.Contains(message.MessageId)) ids.Add(message.MessageId);
                }
                lock (Lock)
                {
                    var signs = SignsFor(chatId);
                    if (sameContent && untrusted.Count >= Limits.SameContentUsers)
                    {
                        if (harmful)
                        {
                            signs.SameContentUsers = Math.Max(signs.SameContentUsers, untrusted.Count);
                            foreach (var u in untrusted) Suspect(u);
                            RememberSpam(chatId, key, sketch, echo: false);
                        }
                        else
                        {
                            // The same plain text from newcomers: a giveaway or a greeting as often as
                            // a raid. Weak suspects: banned only when an admin says it is a raid.
                            signs.EchoUsers = Math.Max(signs.EchoUsers, untrusted.Count);
                            foreach (var u in untrusted) Suspect(u, echoers);
                            RememberSpam(chatId, key, sketch, echo: true);
                        }
                    }
                    else if (knownSpam && senderUntrusted)
                    {
                        // The raid's content, however long after the others.
                        Suspect(user, known == KnownContent.Echo ? echoers : null);
                    }
                    if (print.BareLink && senderUntrusted)
                    {
                        signs.BareLink(user, now);
                        Suspect(user);
                    }
                    if (postedSoon && senderUntrusted) signs.PostedSoonAfterJoining(user, now);
                }
                if (newSuspects.Count > 0) Suspected(chatId, newSuspects);
                if (echoers.Count > 0) Suspected(chatId, echoers, weak: true);
                if (print.BareLink && senderUntrusted && user > 0) ReportLink(ctx);
                Evaluate(chatId);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Raid detection on a message");
            }
        }

        /// <summary>
        /// Who posted, for raid detection: the member, or the channel they posted as. Null for what is
        /// never a raider: the group itself (anonymous admins), its linked channel, Telegram's own
        /// accounts and bots.
        /// </summary>
        private static long? SenderOf(MessageContext ctx)
        {
            var message = ctx.Message;
            if (message.SenderChat is { } channel)
            {
                if (channel.Id == ctx.ChatId || message.IsAutomaticForward || ctx.ChannelPosts?.LinkedChannelId == channel.Id ||
                    ctx.ChannelPosts?.SenderAllowed == true)
                    return null;
                return channel.Id;
            }
            if (message.From == null || message.From.IsBot || ObservationBuffer.Placeholders.Contains(message.From.Id)) return null;
            return message.From.Id;
        }

        /// <summary>
        /// Content that does harm when posted alike: a link or a mention, a file, or a long text. The
        /// same short plain text from several newcomers is an echo, not spam.
        /// </summary>
        internal static bool IsHarmfulContent(Message message, MessageFingerprint print) =>
            print.MediaUniqueId != null || print.LinkCount > 0 ||
            MessageFingerprint.Normalize(message.Text ?? message.Caption).Length >= Limits.LongTextLetters;

        private enum KnownContent { None, Echo, Spam }

        // With the lock held. Content that marked several untrusted members before: harmful spam, or
        // a plain text they echoed.
        private static KnownContent Known(long chatId, string key, HashSet<int> sketch)
        {
            var echo = false;
            if (Incidents.TryGetValue(chatId, out var i))
            {
                if (i.SpamKeys.Contains(key)) return KnownContent.Spam;
                echo = i.EchoKeys.Contains(key);
            }
            if (PendingSpamKeys.TryGetValue(chatId, out var pending) && pending.TryGetValue(key, out var p))
            {
                if (!p.Echo) return KnownContent.Spam;
                echo = true;
            }
            if (sketch != null && SpamSketches.TryGetValue(chatId, out var sketches))
                foreach (var (known, isEcho) in sketches)
                    if (RaidSigns.Similar(known, sketch, Limits.SimilarText))
                    {
                        if (!isEcho) return KnownContent.Spam;
                        echo = true;
                    }
            return echo ? KnownContent.Echo : KnownContent.None;
        }

        /// <summary>
        /// What makes two messages "the same" for raid detection: the text, if it is long enough to
        /// mean something or carries a link (three newcomers saying "hello" is no raid), or the file,
        /// except stickers, which everyone shares. Null when the message cannot be compared.
        /// </summary>
        internal static string ContentKey(Message message, MessageFingerprint print)
        {
            if (print.MediaUniqueId != null)
                return message.Sticker != null ? null : "m:" + print.MediaUniqueId;
            if (print.TextHash == null) return null;
            var letters = MessageFingerprint.Normalize(message.Text ?? message.Caption).Length;
            return letters >= MinComparableLetters || print.LinkCount > 0 ? "t:" + print.TextHash : null;
        }

        /// <summary>Texts shorter than this (in letters) are not compared, links aside.</summary>
        internal const int MinComparableLetters = 20;

        private static async Task OnFloodAsync(long chatId, long userId)
        {
            if (!IsActive(chatId) && ModeOf(chatId) == RaidMode.Off) return;
            if (await TrustOfAsync(chatId, userId) != TrustLevel.New) return;
            lock (Lock) SignsFor(chatId).Flooded(userId, Clock.UtcNow);
            // Their messages go with the ban; a mute leaves the flood to the flood check's own deletes.
            Suspected(chatId, new Dictionary<long, List<int>> { [userId] = new List<int>() });
            Evaluate(chatId);
        }

        /// <summary>
        /// True when raid mode is banning spammers in the chat and <paramref name="userId"/> is untrusted:
        /// their flood is the raid's to deal with (a ban with their messages), not the group's own flood
        /// action as well.
        /// </summary>
        internal static async Task<bool> WillHandleFloodAsync(long chatId, long userId)
        {
            lock (Lock)
            {
                if (!Incidents.TryGetValue(chatId, out var i) || i.Phase != RaidPhase.Active ||
                    !i.Actions.HasFlag(RaidActions.BanSuspects) && !i.Actions.HasFlag(RaidActions.MuteSuspects))
                    return false;
            }
            return await TrustOfAsync(chatId, userId) == TrustLevel.New;
        }

        /// <summary>An @admin report in a group: a burst of them is a sign.</summary>
        internal static void OnReport(long chatId)
        {
            try
            {
                if (ModeOf(chatId) == RaidMode.Off) return;
                lock (Lock) SignsFor(chatId).Reported(Clock.UtcNow);
                Evaluate(chatId);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Raid detection on a report");
            }
        }

        private static async Task<TrustLevel> TrustOfAsync(long chatId, long userId)
        {
            var now = Clock.UtcNow;
            lock (Lock)
            {
                if (TrustSeen.TryGetValue((chatId, userId), out var seen) && now - seen.At < TrustFor) return seen.Level;
            }
            var level = await MemberTrust.OfAsync(chatId, userId);
            // Admins who have not talked much would otherwise look new: their own links reported.
            if (level == TrustLevel.New && userId > 0 && (await AdminIdsAsync(chatId)).Contains(userId)) level = TrustLevel.Admin;
            lock (Lock) TrustSeen[(chatId, userId)] = (level, now);
            return level;
        }

        /// <summary>The chat's admins (<see cref="AdminDirectory"/>); none when Telegram has never said.</summary>
        private static async Task<HashSet<long>> AdminIdsAsync(long chatId) =>
            (await AdminDirectory.GetAsync(chatId))?.Select(a => a.User.Id).ToHashSet() ?? new HashSet<long>();

        // A join added by an admin, from the admin list at hand: the join path never waits on Telegram.
        private static bool IsKnownAdmin(long chatId, long userId) => AdminDirectory.IsKnownAdmin(chatId, userId);

        // Untrusted members who showed raid signs, with the messages that showed them: banned at once
        // in raid mode, muted when auto mode is not yet sure, kept for the button otherwise.
        // Weak ones only echoed a plain text with others (see SuspectAction).
        private static void Suspected(long chatId, Dictionary<long, List<int>> users, bool weak = false)
        {
            var ban = new List<(long, List<int>)>();
            var mute = new List<(long, List<int>)>();
            RaidIncident incident;
            var now = Clock.UtcNow;
            lock (Lock)
            {
                // An admin let them off: not a suspect again for a while.
                foreach (var cleared in users.Keys.Where(u => Cleared.TryGetValue((chatId, u), out var until) && until > now).ToList())
                    users.Remove(cleared);
                if (users.Count == 0) return;
                if (!Incidents.TryGetValue(chatId, out incident))
                {
                    // Remembered for when an incident starts within the next minutes.
                    if (!Pending.TryGetValue(chatId, out var pending)) Pending[chatId] = pending = new Dictionary<long, PendingSuspect>();
                    foreach (var (u, ids) in users)
                    {
                        if (pending.Count >= 500 && !pending.ContainsKey(u)) break;
                        var known = pending.TryGetValue(u, out var p) ? p : null;
                        var kept = known?.Messages ?? new List<int>();
                        kept.AddRange(ids.Where(id => !kept.Contains(id)).Take(20 - kept.Count));
                        pending[u] = new PendingSuspect(now, kept, weak && (known == null || known.Weak));
                    }
                    return;
                }
                foreach (var (u, ids) in users)
                {
                    AddSuspect(incident, u, weak);
                    var kept = AddMessages(incident, u, ids);
                    if (incident.Phase != RaidPhase.Active) continue;
                    switch (ActOn(incident, u))
                    {
                        case SuspectAction.Ban: ban.Add((u, kept)); break;
                        case SuspectAction.Mute: mute.Add((u, kept)); break;
                    }
                }
                incident.LastActivityUtc = now;
            }
            // Kept with the incident, so a restart before the admins answer still knows them.
            Save(incident);
            foreach (var (u, ids) in ban) BanSuspect(chatId, u, ids);
            foreach (var (u, ids) in mute) MuteSuspect(chatId, u, ids);
        }

        private sealed record PendingSuspect(DateTime At, List<int> Messages, bool Weak);

        // With the lock held. A suspect is weak only while nothing harmful was seen from them.
        private static void AddSuspect(RaidIncident incident, long user, bool weak)
        {
            var known = incident.Suspects.Contains(user);
            incident.Suspects.Add(user);
            if (!weak) incident.WeakSuspects.Remove(user);
            else if (!known) incident.WeakSuspects.Add(user);
        }

        private enum SuspectAction { None, Ban, Mute }

        /// <summary>
        /// With the lock held: what raid mode does to a suspect now, and marks it done. Banned when
        /// raid mode bans, except a weak suspect (who only echoed a plain text) in auto mode, which is
        /// never banned on the bot's word alone; an admin turning raid mode on decides it was a raid.
        /// Muted when raid mode mutes suspects, and weak ones when it bans.
        /// </summary>
        private static SuspectAction ActOn(RaidIncident incident, long user)
        {
            var weak = incident.WeakSuspects.Contains(user);
            if (incident.Actions.HasFlag(RaidActions.BanSuspects) && (!weak || incident.AdminDecided))
            {
                if (!incident.Handled.Add(user)) return SuspectAction.None;
                incident.Banned++;
                return SuspectAction.Ban;
            }
            if (!incident.Actions.HasFlag(RaidActions.MuteSuspects) && !incident.Actions.HasFlag(RaidActions.BanSuspects))
                return SuspectAction.None;
            if (incident.Handled.Contains(user) || !incident.MutedSuspects.Add(user)) return SuspectAction.None;
            if (user > 0 && incident.Quarantined.Add(user)) incident.Muted++;
            return SuspectAction.Mute;
        }

        // With the lock held. The suspect's messages so far, the new ones added (up to 20).
        private static List<int> AddMessages(RaidIncident incident, long user, IEnumerable<int> ids)
        {
            if (!incident.SuspectMessages.TryGetValue(user, out var kept)) incident.SuspectMessages[user] = kept = new List<int>();
            foreach (var id in ids)
                if (kept.Count < 20 && !kept.Contains(id)) kept.Add(id);
            return kept.ToList();
        }

        private static readonly Dictionary<long, Dictionary<long, PendingSuspect>> Pending = new();
        private static readonly Dictionary<long, Dictionary<string, (DateTime At, bool Echo)>> PendingSpamKeys = new();
        /// <summary>How long suspects and spam seen before an incident wait for one to start.</summary>
        private static TimeSpan PendingFor => Limits.Window + Limits.Window;

        // With the lock held. Content that marked several untrusted members, kept with the incident
        // (or for the incident about to start).
        private static void RememberSpam(long chatId, string key, HashSet<int> sketch, bool echo)
        {
            if (key == null) return;
            if (sketch != null)
            {
                if (!SpamSketches.TryGetValue(chatId, out var sketches)) SpamSketches[chatId] = sketches = new List<(HashSet<int>, bool)>();
                var same = sketches.FindIndex(k => RaidSigns.Similar(k.Sketch, sketch, Limits.SimilarText));
                if (same < 0 && sketches.Count < 50) sketches.Add((sketch, echo));
                else if (same >= 0 && !echo) sketches[same] = (sketches[same].Sketch, false);
            }
            if (Incidents.TryGetValue(chatId, out var incident))
            {
                if (echo && !incident.SpamKeys.Contains(key)) incident.EchoKeys.Add(key);
                else if (!echo)
                {
                    incident.SpamKeys.Add(key);
                    incident.EchoKeys.Remove(key);
                }
            }
            else
            {
                if (!PendingSpamKeys.TryGetValue(chatId, out var keys)) PendingSpamKeys[chatId] = keys = new Dictionary<string, (DateTime, bool)>();
                var wasEcho = !keys.TryGetValue(key, out var k) || k.Echo;
                if (keys.Count < 100 || keys.ContainsKey(key)) keys[key] = (Clock.UtcNow, echo && wasEcho);
            }
        }

        /// <summary>
        /// An admin looked at <paramref name="userId"/> and let them be (Ignore on a link report): not a
        /// suspect now, and not again for <see cref="RaidLimits.ClearedFor"/>.
        /// </summary>
        internal static void Clear(long chatId, long userId)
        {
            lock (Lock)
            {
                Cleared[(chatId, userId)] = Clock.UtcNow + Limits.ClearedFor;
                if (Pending.TryGetValue(chatId, out var pending)) pending.Remove(userId);
                if (Incidents.TryGetValue(chatId, out var incident))
                {
                    incident.Suspects.Remove(userId);
                    incident.WeakSuspects.Remove(userId);
                }
            }
        }

        // ---------------------------------------------------------------- deciding

        private static void Evaluate(long chatId)
        {
            int score;
            List<string> reasons;
            RaidIncident incident;
            lock (Lock)
            {
                var signs = SignsFor(chatId);
                (score, reasons) = signs.Score(Limits, MemberTrust.JoinsPerHour(chatId));
                Incidents.TryGetValue(chatId, out incident);
                if (incident != null && score > 0)
                {
                    incident.LastActivityUtc = Clock.UtcNow;
                    incident.PeakScore = Math.Max(incident.PeakScore, score);
                    foreach (var r in reasons) if (!incident.Reasons.Contains(r)) incident.Reasons.Add(r);
                }
            }
            if (score < Limits.PossibleScore) return;
            var mode = ModeOf(chatId);
            var wanted = AutoActions(score, reasons);
            if (incident == null)
            {
                if (mode == RaidMode.Off) return;
                lock (Lock)
                {
                    // The admins said it was nothing a moment ago: not again unless it is clearly a raid.
                    if (IgnoredUntil.TryGetValue(chatId, out var until) && until > Clock.UtcNow && score < Limits.RaidScore) return;
                }
                RaidTelemetry.Signal(chatId, score, reasons);
                if (mode == RaidMode.Auto) Activate(chatId, wanted, "auto", null);
                else Alert(chatId, score, reasons);
            }
            else if (incident.Phase == RaidPhase.Alert && mode == RaidMode.Auto)
            {
                Activate(chatId, wanted, "auto", null);
            }
            else if (incident.Phase == RaidPhase.Active && incident.Trigger == "auto" && (incident.Actions | wanted) != incident.Actions)
            {
                // It got worse: hold the spammers back, or close the door and ban.
                Activate(chatId, wanted, incident.Trigger, null);
            }
        }

        /// <summary>
        /// What auto mode does about a score: only harm (spam posted alike, floods, bare links) closes
        /// the group or touches people. Clearly a raid: everything. Harm short of that: new joiners held
        /// back and the suspects muted with their spam deleted. A surge without harm (joins, the same
        /// plain text): new joiners held back, and when it adds up to a raid those echoing it muted too;
        /// never the group closed.
        /// </summary>
        internal static RaidActions AutoActions(int score, IEnumerable<string> reasons)
        {
            var raid = score >= Limits.RaidScore;
            if (!RaidSigns.Harmful(reasons)) return raid ? RaidActions.Quarantine | RaidActions.MuteSuspects : RaidActions.Quarantine;
            return raid ? RaidActions.All : RaidActions.Quarantine | RaidActions.MuteSuspects;
        }

        private static RaidIncident NewIncident(long chatId, RaidPhase phase, string trigger)
        {
            var now = Clock.UtcNow;
            var incident = new RaidIncident { ChatId = chatId, Phase = phase, Trigger = trigger, StartedUtc = now, LastActivityUtc = now };
            // Only what was seen just before: someone suspected an hour ago is not part of this one.
            if (Pending.Remove(chatId, out var pending))
                foreach (var (u, p) in pending.Where(p => now - p.Value.At <= PendingFor))
                {
                    AddSuspect(incident, u, p.Weak);
                    AddMessages(incident, u, p.Messages);
                }
            if (PendingSpamKeys.Remove(chatId, out var keys))
                foreach (var k in keys.Where(k => now - k.Value.At <= PendingFor))
                    (k.Value.Echo ? incident.EchoKeys : incident.SpamKeys).Add(k.Key);
            Incidents[chatId] = incident;
            return incident;
        }

        private static void Alert(long chatId, int score, List<string> reasons)
        {
            RaidIncident incident;
            lock (Lock)
            {
                if (Incidents.ContainsKey(chatId)) return;
                incident = NewIncident(chatId, RaidPhase.Alert, "human");
                incident.PeakScore = score;
                incident.Reasons.AddRange(reasons);
            }
            RaidTelemetry.Started(Snapshot(incident));
            SaveNow(incident);
            SendAlert(incident);
        }

        // The alert, tagging the admins, with the buttons for what to do.
        private static void SendAlert(RaidIncident incident)
        {
            var chatId = incident.ChatId;
            Bot.Dispatch(async () =>
            {
                var lang = Methods.GetGroupLanguage(chatId).Doc;
                var tags = AdminTags(chatId);
                // Waits out a 429 instead of being dropped: a raid the admins never hear about is the
                // worst case in human mode. Rebuilt on each try, so the counts are current; given up
                // once the admins have acted some other way (/raid) or it is over.
                var sent = await SendUntilDeliveredAsync(chatId, () =>
                {
                    int joins, suspects;
                    lock (Lock)
                    {
                        if (!IsCurrent(incident) || incident.Phase != RaidPhase.Alert) return null;
                        joins = SignsFor(chatId).JoinCount;
                        suspects = incident.Suspects.Count;
                    }
                    return (Methods.GetLocaleString(lang, "raidPossible", joins, suspects) + "\n" + tags,
                        Buttons(lang, incident, ("raidLockButton", "lock"), ("raidMuteJoinersButton", "mute"),
                            ("raidBanSuspectsButton", "ban"), ("raidIgnoreButton", "ignore")));
                });
                if (sent != null)
                {
                    lock (Lock) incident.NoticeMessageId = sent.MessageId;
                    Save(incident);
                }
            }, "RaidAlert");
        }

        // With the lock held: still the incident in its chat, not one that ended.
        private static bool IsCurrent(RaidIncident incident) =>
            Incidents.TryGetValue(incident.ChatId, out var now) && ReferenceEquals(now, incident);

        /// <summary>
        /// Sends what <paramref name="build"/> gives, waiting out 429s (for up to half an hour) rather
        /// than dropping it as the usual send helpers drop messages with buttons. <paramref name="build"/>
        /// returning null means it is no longer wanted. Null when nothing went out.
        /// </summary>
        private static async Task<Message> SendUntilDeliveredAsync(long chatId, Func<(string Text, InlineKeyboardMarkup Markup)?> build)
        {
            var giveUp = Clock.UtcNow + TimeSpan.FromMinutes(30);
            var parseMode = ParseMode.Html;
            while (true)
            {
                if (build() is not { } content) return null;
                try
                {
                    Interlocked.Increment(ref Bot.MessagesSent);
                    return await Bot.Api.SendMessage(chatId, content.Text, parseMode,
                        linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true }, replyMarkup: content.Markup);
                }
                catch (Exception e)
                {
                    var api = Bot.AsApiError(e);
                    if (Bot.IsRateLimited(api) && Clock.UtcNow < giveUp)
                    {
                        var wait = Bot.Flood.PausedFor(Lane.Message, chatId.ToString());
                        if (api.Parameters?.RetryAfter is { } retryAfter && TimeSpan.FromSeconds(retryAfter) > wait)
                            wait = TimeSpan.FromSeconds(retryAfter);
                        await Clock.Delay(wait + TimeSpan.FromSeconds(1));
                        continue;
                    }
                    if (Bot.IsParseFailure(api) && parseMode == ParseMode.Html)
                    {
                        parseMode = ParseMode.None;
                        continue;
                    }
                    // A group that removed the bot, or blocked it from posting: nobody to tell.
                    if (!Bot.IsUnreachable(api)) ErrorReporter.Report(e, $"Raid message to {chatId}");
                    return null;
                }
            }
        }

        /// <summary>Turns raid mode on (or adds to what it does) in a chat. <paramref name="by"/> is the admin, if one asked.</summary>
        internal static void Activate(long chatId, RaidActions actions, string trigger, long? by)
        {
            RaidIncident incident;
            var ban = new List<(long, List<int>)>();
            var mute = new List<(long, List<int>)>();
            bool lockNow, firstTime;
            lock (Lock)
            {
                Incidents.TryGetValue(chatId, out incident);
                incident ??= NewIncident(chatId, RaidPhase.Active, trigger);
                firstTime = incident.Phase != RaidPhase.Active || incident.ActiveSinceUtc == null;
                incident.Phase = RaidPhase.Active;
                incident.ActiveSinceUtc ??= Clock.UtcNow;
                if (firstTime && incident.Trigger == "human") incident.Trigger = trigger;
                incident.Actions |= actions;
                if (by.HasValue) incident.AdminDecided = true;
                incident.LastActivityUtc = Clock.UtcNow;
                lockNow = incident.Actions.HasFlag(RaidActions.Lock) && !incident.Locked;
                if (lockNow)
                {
                    incident.Locked = true;
                    Locking[incident.IncidentId] = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                }
                foreach (var s in incident.Suspects)
                {
                    var ids = incident.SuspectMessages.TryGetValue(s, out var kept) ? kept.ToList() : new List<int>();
                    switch (ActOn(incident, s))
                    {
                        case SuspectAction.Ban: ban.Add((s, ids)); break;
                        case SuspectAction.Mute: mute.Add((s, ids)); break;
                    }
                }
            }
            if (firstTime) RaidTelemetry.Activated(Snapshot(incident), by);
            SaveNow(incident);
            if (lockNow) Bot.Dispatch(() => LockAsync(incident), "RaidLock");
            foreach (var (u, ids) in ban) BanSuspect(chatId, u, ids);
            foreach (var (u, ids) in mute) MuteSuspect(chatId, u, ids);
            UpdateNotice(chatId, force: true, tagAdmins: firstTime);
        }

        // The group's own permissions are saved first, so they can be put back as they were.
        private static async Task LockAsync(RaidIncident incident)
        {
            var queued = false;
            try
            {
                queued = await PutInLockAsync(incident);
            }
            finally
            {
                TaskCompletionSource<bool> locking;
                lock (Lock) Locking.Remove(incident.IncidentId, out locking);
                locking?.TrySetResult(queued);
            }
        }

        private static async Task<bool> PutInLockAsync(RaidIncident incident)
        {
            var chatId = incident.ChatId;
            // A permission change still waiting in the queue (the last raid's unlock, held up by a 429)
            // is what the group will have: Telegram's answer would be the lock still in place.
            var pending = EnforcementQueue.PendingPermissions(chatId);
            if (pending != null && pending.CanSendMessages != false)
            {
                lock (Lock) incident.SavedPermissions = pending;
            }
            else
            {
                try
                {
                    // Read now, not from ChatDetails: these are what the group gets back.
                    var chat = await Bot.Api.GetChat(chatId);
                    ChatDetails.Remember(chat);
                    lock (Lock) incident.SavedPermissions = chat.Permissions;
                }
                catch (Exception e)
                {
                    // Locked all the same: stopping the raid comes first. Unlocking then gives the usual
                    // permissions back, and the summary says to check them.
                    LogHelper.Error($"Could not read the permissions of {chatId} before locking: {e.Message}");
                }
            }
            // Ended while the permissions were read: no lock, and so no unlock either.
            lock (Lock)
                if (!Incidents.TryGetValue(chatId, out var current) || current != incident) return false;
            SaveNow(incident);
            var job = EnforcementJob.SetPermissions(chatId, EnforcementExecutor.NothingAllowed);
            job.Note.Reason = "Raid lock";
            RaidTelemetry.Action(chatId, "lock", 0);
            var done = await EnforcementQueue.SubmitAsync(job);
            // When the door actually closed, for the telemetry: how long it took under the 429s.
            // A continuation, not an await, so nothing sits waiting on the queue meanwhile.
            _ = done.ContinueWith(t =>
            {
                if (t.Status == TaskStatus.RanToCompletion && t.Result.Outcome == EnforcementOutcome.Done)
                    RaidTelemetry.Action(chatId, "locked", 0);
            }, TaskScheduler.Default);
            return true;
        }

        private static void Quarantine(long chatId, long userId)
        {
            var job = EnforcementJob.Mute(chatId, userId, Clock.UtcNow + Limits.QuarantineFor).For("Raid quarantine", null);
            job.Note.NewJoiner = true;
            Bot.Dispatch(async () => await EnforcementQueue.SubmitAsync(job), "RaidQuarantine");
            RaidTelemetry.Action(chatId, "quarantine", userId);
        }

        private static void BanSuspect(long chatId, long userId, List<int> messages)
        {
            if (userId < 0)
            {
                BanChannel(chatId, userId, messages);
                return;
            }
            var job = EnforcementJob.Ban(chatId, userId, revokeMessages: true).For("Raid", null);
            Bot.Dispatch(async () => await EnforcementQueue.SubmitAsync(job), "RaidBan");
            RaidTelemetry.Action(chatId, "ban", userId);
        }

        // Held back until it is clearly a raid: muted like a joiner, and the spam that made them a suspect deleted.
        private static void MuteSuspect(long chatId, long userId, List<int> messages)
        {
            if (userId < 0)
            {
                // A channel cannot be muted, only banned; a channel spamming the group is no member to spare.
                BanChannel(chatId, userId, messages);
                return;
            }
            var job = EnforcementJob.Mute(chatId, userId, Clock.UtcNow + Limits.QuarantineFor).For("Raid suspect", null);
            Bot.Dispatch(async () =>
            {
                await EnforcementQueue.SubmitAsync(job);
                if (messages.Count > 0) await EnforcementQueue.SubmitAsync(EnforcementJob.Delete(chatId, messages.ToArray()));
            }, "RaidMute");
            RaidTelemetry.Action(chatId, "mute", userId);
        }

        // A channel the raid posted as: banned from the group and its posts deleted, unless it is the
        // group's own linked channel, which is never a raider.
        private static void BanChannel(long chatId, long channelId, List<int> messages)
        {
            Bot.Dispatch(async () =>
            {
                if (await LinkedChannelAsync(chatId) == channelId) return;
                await EnforcementQueue.SubmitAsync(EnforcementJob.BanSenderChat(chatId, channelId).For("Raid", null));
                if (messages.Count > 0) await EnforcementQueue.SubmitAsync(EnforcementJob.Delete(chatId, messages.ToArray()));
            }, "RaidBanChannel");
            RaidTelemetry.Action(chatId, "ban", channelId);
        }

        private static Task<long> LinkedChannelAsync(long chatId) => ChatDetails.LinkedChannelAsync(chatId);

        // ---------------------------------------------------------------- ending

        private static void Tick()
        {
            var now = Clock.UtcNow;
            List<RaidIncident> quiet;
            List<long> notices;
            lock (Lock)
            {
                quiet = Incidents.Values.Where(i => now - i.LastActivityUtc >= Limits.QuietEnd).ToList();
                notices = Incidents.Values.Where(i => i.Phase == RaidPhase.Active && now - i.NoticeUpdatedUtc >= Limits.NoticeEvery)
                    .Select(i => i.ChatId).ToList();
                foreach (var key in LinkReported.Where(p => now - p.Value > Limits.LinkReportEvery).Select(p => p.Key).ToList())
                    LinkReported.Remove(key);
                foreach (var key in TrustSeen.Where(p => now - p.Value.At > TrustFor).Select(p => p.Key).ToList())
                    TrustSeen.Remove(key);
                foreach (var key in Cleared.Where(p => p.Value <= now).Select(p => p.Key).ToList()) Cleared.Remove(key);
                foreach (var key in IgnoredUntil.Where(p => p.Value <= now).Select(p => p.Key).ToList()) IgnoredUntil.Remove(key);
                foreach (var (chat, pending) in Pending.ToList())
                {
                    foreach (var u in pending.Where(p => now - p.Value.At > PendingFor).Select(p => p.Key).ToList()) pending.Remove(u);
                    if (pending.Count == 0) Pending.Remove(chat);
                }
                foreach (var (chat, keys) in PendingSpamKeys.ToList())
                {
                    foreach (var k in keys.Where(p => now - p.Value.At > PendingFor).Select(p => p.Key).ToList()) keys.Remove(k);
                    if (keys.Count == 0) PendingSpamKeys.Remove(chat);
                }
            }
            MentionResolver.Prune();
            Detection.Profiles.Prune();
            AdminDirectory.Prune();
            ChatDetails.Prune();
            // One failing incident must not stop the others ending, or restart the loop.
            foreach (var i in quiet)
            {
                try { End(i.ChatId, null, quietly: i.Phase == RaidPhase.Alert); }
                catch (Exception e) { ErrorReporter.Report(e, $"Ending the raid in {i.ChatId}"); }
            }
            foreach (var chat in notices.Except(quiet.Select(i => i.ChatId))) UpdateNotice(chat, force: false, tagAdmins: false);
        }

        internal static string ReleaseKey(long chatId, string shortId) => $"raidq:{Edition}:{chatId}:{shortId}";

        /// <summary>
        /// Ends the incident in a chat: puts the group's permissions back if we locked it and posts the
        /// summary. <paramref name="quietly"/> ends an alert nobody acted on without a word; when an
        /// admin did that (<paramref name="by"/>, the Ignore button), its suspects are let off and no
        /// new alert comes for half an hour unless it is clearly a raid.
        /// </summary>
        internal static void End(long chatId, long? by, bool quietly = false)
        {
            RaidIncident incident;
            var now = Clock.UtcNow;
            lock (Lock)
            {
                if (!Incidents.Remove(chatId, out incident)) return;
                Signs.Remove(chatId);
                SpamSketches.Remove(chatId);
                if (quietly && by.HasValue)
                {
                    foreach (var s in incident.Suspects) Cleared[(chatId, s)] = now + Limits.ClearedFor;
                    IgnoredUntil[chatId] = now + IgnoreFor;
                }
            }
            Persisting.Wait();
            try
            {
                Redis.db.KeyDeleteAsync(IncidentKey(chatId)).GetAwaiter().GetResult();
                Redis.db.SetRemoveAsync(ActiveSetKey, chatId).GetAwaiter().GetResult();
                if (incident.Quarantined.Count > 0)
                {
                    // For the summary's button: exactly the people this raid muted, for as long as the mute lasts.
                    var key = ReleaseKey(chatId, incident.ShortId);
                    foreach (var u in incident.Quarantined) Redis.db.SetAddAsync(key, u).GetAwaiter().GetResult();
                    Redis.db.KeyExpireAsync(key, Limits.QuarantineFor + TimeSpan.FromHours(1)).GetAwaiter().GetResult();
                }
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Forgetting the raid in {chatId}");
            }
            finally
            {
                Persisting.Release();
            }
            RaidTelemetry.Ended(Snapshot(incident), by, ignored: quietly && by.HasValue);
            // An admin said it was no raid: its suspects were fine (members only; a channel is no one to label).
            if (quietly && by is { } admin)
                foreach (var suspect in incident.Suspects.Where(s => s > 0))
                    Detection.Decisions.Fine(chatId, suspect, admin, new[] { "raid" });
            // Detection's reports held back while it was on, in one.
            Detection.DetectionReports.RaidEnded(chatId);
            if (incident.Locked)
            {
                Task<bool> locking;
                lock (Lock) locking = Locking.TryGetValue(incident.IncidentId, out var pending) ? pending.Task : Task.FromResult(true);
                Bot.Dispatch(async () =>
                {
                    Clock.Park();
                    bool locked;
                    try { locked = await locking; }
                    finally { Clock.Unpark(); }
                    if (!locked) return;
                    // Read after the lock was queued: that is when the group's own permissions are known.
                    var job = EnforcementJob.SetPermissions(chatId, incident.SavedPermissions ?? UsualPermissions);
                    job.Note.Reason = "Raid over";
                    await EnforcementQueue.SubmitAsync(job);
                }, "RaidUnlock");
            }
            if (incident.NoticeMessageId is { } notice && !(quietly && by.HasValue))
            {
                // The alert's or notice's buttons would do nothing now (Ignore clears its own).
                Bot.Dispatch(async () =>
                {
                    try { await Bot.Api.EditMessageReplyMarkup(chatId, notice, null); }
                    catch (Exception) { /* deleted by an admin, or not ours to edit any more: nothing to clear */ }
                }, "RaidNoticeButtons");
            }
            if (quietly || incident.Phase != RaidPhase.Active) return;
            Bot.Dispatch(async () =>
            {
                var lang = Methods.GetGroupLanguage(chatId).Doc;
                var text = Methods.GetLocaleString(lang, "raidEnded", incident.Joined, incident.Muted, incident.Banned,
                    (int)Limits.QuarantineFor.TotalHours);
                var markup = incident.Quarantined.Count > 0 ? Buttons(lang, incident, ("raidReleaseButton", "release")) : null;
                await SendUntilDeliveredAsync(chatId, () => (text, markup));
            }, "RaidEnded");
        }

        /// <summary>What a group gets back when its own permissions could not be read before the lock.</summary>
        internal static readonly ChatPermissions UsualPermissions = new ChatPermissions
        {
            CanSendMessages = true, CanSendAudios = true, CanSendDocuments = true, CanSendPhotos = true, CanSendVideos = true,
            CanSendVideoNotes = true, CanSendVoiceNotes = true, CanSendPolls = true, CanSendOtherMessages = true,
            CanAddWebPagePreviews = true, CanInviteUsers = true
        };

        /// <summary>
        /// Unmutes everyone the raid <paramref name="shortId"/> muted in the chat (the summary's button):
        /// not anyone the group itself muted on joining. False when there is nobody left to let go.
        /// </summary>
        internal static bool Release(long chatId, string shortId, long by)
        {
            var key = ReleaseKey(chatId, shortId);
            // Not those the group itself mutes on joining: it wants them muted until an admin says otherwise.
            var groupMuted = Redis.db.SetMembersAsync($"chat:{chatId}:mutedJoiners").GetAwaiter().GetResult()
                .Select(v => v.ToString()).ToHashSet();
            var users = Redis.db.SetMembersAsync(key).GetAwaiter().GetResult()
                .Where(v => !groupMuted.Contains(v.ToString()))
                .Select(v => long.TryParse(v.ToString(), out var id) ? id : 0).Where(id => id != 0).ToList();
            Redis.db.KeyDeleteAsync(key).GetAwaiter().GetResult();
            foreach (var u in users)
                Bot.Dispatch(async () => await EnforcementQueue.SubmitAsync(EnforcementJob.Unmute(chatId, u).For("Raid over", by)), "RaidRelease");
            return users.Count > 0;
        }

        // ---------------------------------------------------------------- telling people

        private static void UpdateNotice(long chatId, bool force, bool tagAdmins)
        {
            RaidIncident incident;
            lock (Lock)
            {
                if (!Incidents.TryGetValue(chatId, out incident) || incident.Phase != RaidPhase.Active) return;
                if (!force && Clock.UtcNow - incident.NoticeUpdatedUtc < Limits.NoticeEvery) return;
                incident.NoticeUpdatedUtc = Clock.UtcNow;
            }
            Bot.Dispatch(async () =>
            {
                try
                {
                    var lang = Methods.GetGroupLanguage(chatId).Doc;
                    var tags = tagAdmins ? AdminTags(chatId) : null;
                    (string, InlineKeyboardMarkup)? Build()
                    {
                        string text;
                        lock (Lock)
                        {
                            if (!IsCurrent(incident)) return null;
                            text = Methods.GetLocaleString(lang, "raidActive", incident.Joined, incident.Muted, incident.Banned,
                                (int)Limits.QuarantineFor.TotalHours);
                            if (incident.Locked) text += "\n" + Methods.GetLocaleString(lang, "raidLocked");
                        }
                        if (tags != null) text += "\n" + tags;
                        return (text, Buttons(lang, incident, ("raidEndButton", "end")));
                    }
                    int? existing;
                    lock (Lock) existing = incident.NoticeMessageId;
                    if (existing is { } id && !tagAdmins)
                    {
                        // An update to the counts: a 429 just leaves it for the next one.
                        if (Build() is { } content)
                        {
                            try { await Bot.Edit(chatId, id, content.Item1, content.Item2); }
                            catch (Exception) { /* rate limited or deleted: the next update tries again, or it ends */ }
                        }
                        return;
                    }
                    // Raid mode going on, with the admins tagged: must get through.
                    var sent = await SendUntilDeliveredAsync(chatId, Build);
                    if (sent != null)
                    {
                        lock (Lock) incident.NoticeMessageId = sent.MessageId;
                        Save(incident);
                    }
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Raid notice in {chatId}");
                }
            }, "RaidNotice");
        }

        // Every admin, tagged, so their phones go off.
        private static string AdminTags(long chatId)
        {
            try
            {
                var admins = AdminDirectory.Get(chatId);
                if (admins == null) return "";
                return string.Join(" ", admins.Where(a => !a.User.IsBot)
                    .Select(a => $"<a href=\"tg://user?id={a.User.Id}\">{a.User.FirstName.FormatHTML()}</a>"));
            }
            catch (Exception e)
            {
                LogHelper.Error($"Could not list the admins of {chatId} to tag them: {e.Message}");
                return "";
            }
        }

        // raid:{chat}:{action}:{incident}, so a button from an earlier raid does nothing to this one.
        private static InlineKeyboardMarkup Buttons(XDocument lang, RaidIncident incident, params (string Key, string Action)[] buttons) =>
            new InlineKeyboardMarkup(buttons.Select(b =>
                new[] { InlineKeyboardButton.WithCallbackData(Methods.GetLocaleString(lang, b.Key), $"raid:{incident.ChatId}:{b.Action}:{incident.ShortId}") }));

        // An untrusted member posted nothing but a link: the admins decide.
        private static void ReportLink(MessageContext ctx)
        {
            var chatId = ctx.ChatId;
            var user = ctx.UserId;
            lock (Lock)
            {
                // The alert or raid mode is about them already: they are among its suspects.
                if (Incidents.ContainsKey(chatId)) return;
                if (LinkReported.TryGetValue((chatId, user), out var at) && Clock.UtcNow - at < Limits.LinkReportEvery) return;
                LinkReported[(chatId, user)] = Clock.UtcNow;
            }
            var message = ctx.Message;
            var reportsOff = ctx.Setting("Flagged").Equals("yes"); // the group turned reports off
            Detection.DetectionEngine.Checked(chatId, user, message, Detection.SignCatalog.RaidLink, reportsOff ? "none" : "report",
                reported: !reportsOff);
            if (reportsOff) return;
            Bot.Dispatch(() =>
            {
                var lang = ctx.Lang ?? Methods.GetGroupLanguage(chatId).Doc;
                var name = message.From.FirstName;
                var text = Methods.GetLocaleString(lang, "raidSuspectLink", name);
                var markup = new InlineKeyboardMarkup(new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData(Methods.GetLocaleString(lang, "raidBanButton"), $"raidlink:{chatId}:ban:{user}:{message.MessageId}"),
                        InlineKeyboardButton.WithCallbackData(Methods.GetLocaleString(lang, "raidIgnoreButton"), $"raidlink:{chatId}:ignore:{user}:{message.MessageId}")
                    }
                });
                Bot.SendReply(text, chatId, message.MessageId, markup);
                return Task.CompletedTask;
            }, "RaidLinkReport");
            RaidTelemetry.Action(chatId, "link_report", user);
        }

        // A copy for anything outside this class: the incident's sets and lists change under the
        // lock while it runs, so nobody else may read them as they are.
        private static RaidIncident Snapshot(RaidIncident incident)
        {
            lock (Lock) return RaidIncident.FromJson(incident.ToJson());
        }

        // ---------------------------------------------------------------- persistence

        // Writes to the incident's keys, one at a time: a delayed save must never land after End
        // deleted them, or a restart would bring back a raid that is over.
        private static readonly SemaphoreSlim Persisting = new(1, 1);
        private static readonly HashSet<string> SavePending = new();
        /// <summary>How long changes to an incident gather before they are written.</summary>
        internal static TimeSpan SaveEvery = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Saves the incident within <see cref="SaveEvery"/>: in a raid it changes with every suspect,
        /// and writing the whole of it each time made the bytes written grow with the square of the
        /// raid. What a restart must not lose at all (the incident starting, raid mode going on, the
        /// permissions saved before the lock) goes through <see cref="SaveNow"/>.
        /// </summary>
        private static void Save(RaidIncident incident)
        {
            lock (Lock)
            {
                if (!SavePending.Add(incident.IncidentId)) return;
            }
            Bot.Dispatch(async () =>
            {
                await Clock.Delay(SaveEvery);
                lock (Lock) SavePending.Remove(incident.IncidentId);
                SaveNow(incident);
            }, "RaidSave");
        }

        private static void SaveNow(RaidIncident incident)
        {
            Persisting.Wait();
            try
            {
                string json;
                lock (Lock)
                {
                    // Over already: End deleted it, and it stays deleted.
                    if (!IsCurrent(incident)) return;
                    json = incident.ToJson();
                }
                Redis.db.StringSetAsync(IncidentKey(incident.ChatId), json, null, false, When.Always, CommandFlags.None).GetAwaiter().GetResult();
                Redis.db.SetAddAsync(ActiveSetKey, incident.ChatId).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Saving the raid in {incident.ChatId}");
            }
            finally
            {
                Persisting.Release();
            }
        }

        private static async Task LoadAsync()
        {
            try
            {
                var chats = await Redis.db.SetMembersAsync(ActiveSetKey);
                foreach (var chat in chats ?? Array.Empty<RedisValue>())
                {
                    if (!long.TryParse(chat.ToString(), out var chatId)) continue;
                    var json = await Redis.db.StringGetAsync(IncidentKey(chatId));
                    if (json.IsNullOrEmpty)
                    {
                        await Redis.db.SetRemoveAsync(ActiveSetKey, chatId);
                        continue;
                    }
                    var incident = RaidIncident.FromJson(json.ToString());
                    // Never over one in memory: that one is newer (the loop restarting after a fault
                    // loads again).
                    bool added;
                    lock (Lock) added = Incidents.TryAdd(chatId, incident);
                    // A restart between starting the incident and posting about it: the admins have
                    // not heard yet, so they hear now.
                    if (!added || incident.NoticeMessageId != null) continue;
                    if (incident.Phase == RaidPhase.Alert) SendAlert(incident);
                    else UpdateNotice(chatId, force: true, tagAdmins: true);
                }
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Loading raids in progress");
            }
        }

        /// <summary>Forgets everything in memory, as a restart does; Redis keeps the incidents. For tests.</summary>
        internal static void ResetForTests()
        {
            lock (Lock)
            {
                Signs.Clear();
                Incidents.Clear();
                foreach (var l in Locking.Values) l.TrySetResult(false);
                Locking.Clear();
                LinkReported.Clear();
                TrustSeen.Clear();
                Modes.Clear();
                Pending.Clear();
                PendingSpamKeys.Clear();
                Cleared.Clear();
                IgnoredUntil.Clear();
                SpamSketches.Clear();
                SavePending.Clear();
            }
            Limits = new RaidLimits();
            MentionResolver.ResetForTests();
            Detection.Profiles.ResetForTests();
            AdminDirectory.ResetForTests();
            ChatDetails.ResetForTests();
            LogDigest.ResetForTests();
            GroupSettings.ResetForTests();
        }

        /// <summary>Reloads the incidents from Redis, as a restart does. For tests.</summary>
        internal static Task ReloadForTests() => LoadAsync();
    }
}
