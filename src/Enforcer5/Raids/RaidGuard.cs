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
    /// established members never count) the same text or image from several of them, floods, bare
    /// links, posting the moment they joined; and a burst of @admin reports. They add up to a score.
    /// </para>
    /// <para>
    /// <b>Acting</b>, by the group's <see cref="RaidMode"/>: in human mode (the default) a possible raid
    /// tags the admins and asks what to do (lock the group, mute new joiners, ban the suspects, or
    /// ignore); in auto mode raid mode goes on at once and the admins are told. <c>/raid</c> turns it
    /// on in any mode. Raid mode can lock the group (only admins can post; its own permissions are
    /// saved and put back), mutes everyone who joins for a day, and bans untrusted members showing
    /// raid signs, deleting their messages in the same call. All of it goes through the enforcement
    /// queue, so none of it is lost to a 429. It ends after <see cref="RaidLimits.QuietEnd"/> without
    /// joins or signs, or with <c>/raid off</c> or its button; if the raid keeps coming, so does raid
    /// mode.
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
        private static readonly Dictionary<(long, long), DateTime> LinkReported = new();
        private static readonly Dictionary<(long, long), (TrustLevel Level, DateTime At)> TrustSeen = new();
        private static readonly Dictionary<long, (RaidMode Mode, DateTime At)> Modes = new();
        private static readonly TimeSpan TrustFor = TimeSpan.FromMinutes(5);
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
            FloodTracker.Flooded += (chat, user) => Bot.Dispatch(() => OnFloodAsync(chat, user), "RaidFlood");
            RaidCapture.RaidOn = IsActive;
        }

        /// <summary>True while raid mode is on in the chat.</summary>
        internal static bool IsActive(long chatId)
        {
            lock (Lock) return Incidents.TryGetValue(chatId, out var i) && i.Phase == RaidPhase.Active;
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
                lock (Lock)
                {
                    var signs = SignsFor(chatId);
                    signs.Joined(join.UserId, Clock.UtcNow);
                    Incidents.TryGetValue(chatId, out var incident);
                    if (incident != null)
                    {
                        incident.Joined++;
                        incident.Joiners.Add(join.UserId);
                        incident.LastActivityUtc = Clock.UtcNow;
                    }
                    quarantine = incident is { Phase: RaidPhase.Active } && incident.Actions.HasFlag(RaidActions.Quarantine) &&
                                 incident.Quarantined.Add(join.UserId);
                    if (quarantine) incident.Muted++;
                }
                RaidTelemetry.Joined(join);
                if (quarantine) Quarantine(chatId, join.UserId);
                if (ModeOf(chatId) != RaidMode.Off || IsActive(chatId)) Evaluate(chatId);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Raid detection on a join");
            }
        }

        /// <summary>
        /// Every group message on the moderation path. Cheap unless the message looks like a sign:
        /// the trust lookup (a few Redis reads) only happens for bare links, content several people
        /// posted, and joiners posting at once.
        /// </summary>
        internal static async Task OnMessageAsync(MessageContext ctx)
        {
            try
            {
                var message = ctx.Message;
                if (!ctx.IsGroup || message.From == null || ObservationBuffer.Placeholders.Contains(message.From.Id) ||
                    message.NewChatMembers != null || message.LeftChatMember != null || message.From.IsBot)
                    return;
                var chatId = ctx.ChatId;
                var active = IsActive(chatId);
                if (!active && ModeOf(chatId) == RaidMode.Off) return;
                var user = message.From.Id;
                var now = Clock.UtcNow;
                var print = MessageFingerprint.Of(message);
                var key = ContentKey(message, print);

                IReadOnlyCollection<long> posters = Array.Empty<long>();
                DateTime? joinedAt;
                var knownSpam = false;
                lock (Lock)
                {
                    var signs = SignsFor(chatId);
                    if (key != null) posters = signs.Posted(key, user, now);
                    joinedAt = signs.JoinedAt(user, now);
                    knownSpam = key != null && Incidents.TryGetValue(chatId, out var i) && i.SpamKeys.Contains(key);
                }
                var postedSoon = joinedAt.HasValue && now - joinedAt.Value <= Limits.PostSoonAfterJoin;
                var sameContent = posters.Count >= Limits.SameContentUsers;
                if (!sameContent && !print.BareLink && !postedSoon && !knownSpam)
                {
                    RaidTelemetry.Message(chatId, user, print, null);
                    return;
                }

                // Who among them is untrusted.
                var untrusted = new List<long>();
                foreach (var poster in (sameContent ? posters : new[] { user }).Take(50))
                    if (await TrustOfAsync(chatId, poster) == TrustLevel.New) untrusted.Add(poster);
                var senderUntrusted = untrusted.Contains(user);
                RaidTelemetry.Message(chatId, user, print, senderUntrusted);

                var newSuspects = new List<long>();
                lock (Lock)
                {
                    var signs = SignsFor(chatId);
                    if (sameContent && untrusted.Count >= Limits.SameContentUsers)
                    {
                        signs.SameContentUsers = Math.Max(signs.SameContentUsers, untrusted.Count);
                        signs.SameContentFromUntrusted(now);
                        newSuspects.AddRange(untrusted);
                        RememberSpam(chatId, key);
                    }
                    else if (knownSpam && senderUntrusted)
                    {
                        // The raid's content, however long after the others.
                        signs.SameContentFromUntrusted(now);
                        newSuspects.Add(user);
                    }
                    if (print.BareLink && senderUntrusted)
                    {
                        signs.BareLink(user, now);
                        newSuspects.Add(user);
                    }
                    if (postedSoon && senderUntrusted) signs.PostedSoonAfterJoining(user, now);
                }
                if (newSuspects.Count > 0) Suspected(chatId, newSuspects, message);
                if (print.BareLink && senderUntrusted) ReportLink(ctx);
                Evaluate(chatId);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Raid detection on a message");
            }
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
            var letters = (message.Text ?? message.Caption ?? "").Count(char.IsLetter);
            return letters >= MinComparableLetters || print.LinkCount > 0 ? "t:" + print.TextHash : null;
        }

        /// <summary>Texts shorter than this (in letters) are not compared, links aside.</summary>
        internal const int MinComparableLetters = 20;

        private static async Task OnFloodAsync(long chatId, long userId)
        {
            if (!IsActive(chatId) && ModeOf(chatId) == RaidMode.Off) return;
            if (await TrustOfAsync(chatId, userId) != TrustLevel.New) return;
            lock (Lock) SignsFor(chatId).Flooded(userId, Clock.UtcNow);
            Suspected(chatId, new[] { userId }, null);
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
                if (!Incidents.TryGetValue(chatId, out var i) || i.Phase != RaidPhase.Active || !i.Actions.HasFlag(RaidActions.BanSuspects))
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
            lock (Lock) TrustSeen[(chatId, userId)] = (level, now);
            return level;
        }

        // Untrusted members who showed raid signs: banned at once in raid mode, kept for the button otherwise.
        private static void Suspected(long chatId, IEnumerable<long> users, Message message)
        {
            var ban = new List<long>();
            RaidIncident incident;
            lock (Lock)
            {
                if (!Incidents.TryGetValue(chatId, out incident))
                {
                    // Remembered for when an incident starts within the minute.
                    foreach (var u in users) PendingSuspects(chatId).Add(u);
                    return;
                }
                foreach (var u in users)
                {
                    incident.Suspects.Add(u);
                    if (incident.Phase == RaidPhase.Active && incident.Actions.HasFlag(RaidActions.BanSuspects) && incident.Handled.Add(u))
                    {
                        ban.Add(u);
                        incident.Banned++;
                    }
                }
                incident.LastActivityUtc = Clock.UtcNow;
            }
            // Kept with the incident, so a restart before the admins answer still knows them.
            Save(incident);
            foreach (var u in ban) BanSuspect(chatId, u);
        }

        private static readonly Dictionary<long, HashSet<long>> Pending = new();
        private static readonly Dictionary<long, HashSet<string>> PendingSpamKeys = new();

        // With the lock held. Content that marked several untrusted members, kept with the incident
        // (or for the incident about to start).
        private static void RememberSpam(long chatId, string key)
        {
            if (key == null) return;
            if (Incidents.TryGetValue(chatId, out var incident)) incident.SpamKeys.Add(key);
            else
            {
                if (!PendingSpamKeys.TryGetValue(chatId, out var set)) PendingSpamKeys[chatId] = set = new HashSet<string>();
                if (set.Count < 100) set.Add(key);
            }
        }

        private static HashSet<long> PendingSuspects(long chatId)
        {
            if (!Pending.TryGetValue(chatId, out var set)) Pending[chatId] = set = new HashSet<long>();
            if (set.Count > 500) set.Clear();
            return set;
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
            if (incident == null)
            {
                if (mode == RaidMode.Off) return;
                RaidTelemetry.Signal(chatId, score, reasons);
                // Auto mode: a raid gets everything; a possible one only holds new joiners back,
                // and nobody is banned until it is clearly a raid.
                if (mode == RaidMode.Auto)
                    Activate(chatId, score >= Limits.RaidScore ? RaidActions.All : RaidActions.Quarantine, "auto", null);
                else
                    Alert(chatId, score, reasons);
            }
            else if (incident.Phase == RaidPhase.Alert && mode == RaidMode.Auto && score >= Limits.RaidScore)
            {
                Activate(chatId, RaidActions.All, "auto", null);
            }
            else if (incident.Phase == RaidPhase.Active && score >= Limits.RaidScore && incident.Actions != RaidActions.All &&
                     incident.Trigger == "auto")
            {
                // It got worse: close the door and ban.
                Activate(chatId, RaidActions.All, incident.Trigger, null);
            }
        }

        private static RaidIncident NewIncident(long chatId, RaidPhase phase, string trigger)
        {
            var now = Clock.UtcNow;
            var incident = new RaidIncident { ChatId = chatId, Phase = phase, Trigger = trigger, StartedUtc = now, LastActivityUtc = now };
            if (Pending.Remove(chatId, out var pending)) foreach (var u in pending) incident.Suspects.Add(u);
            if (PendingSpamKeys.Remove(chatId, out var keys)) foreach (var k in keys) incident.SpamKeys.Add(k);
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
            Save(incident);
            Bot.Dispatch(() =>
            {
                var lang = Methods.GetGroupLanguage(chatId).Doc;
                int joins, suspects;
                lock (Lock)
                {
                    joins = SignsFor(chatId).JoinCount;
                    suspects = incident.Suspects.Count;
                }
                var text = Methods.GetLocaleString(lang, "raidPossible", joins, suspects) + "\n" + AdminTags(chatId);
                var sent = Bot.Send(text, chatId, Buttons(lang, chatId, ("raidLockButton", "lock"), ("raidMuteJoinersButton", "mute"),
                    ("raidBanSuspectsButton", "ban"), ("raidIgnoreButton", "ignore")));
                if (sent != null)
                {
                    lock (Lock) incident.NoticeMessageId = sent.MessageId;
                    Save(incident);
                }
                return Task.CompletedTask;
            }, "RaidAlert");
        }

        /// <summary>Turns raid mode on (or adds to what it does) in a chat. <paramref name="by"/> is the admin, if one asked.</summary>
        internal static void Activate(long chatId, RaidActions actions, string trigger, long? by)
        {
            RaidIncident incident;
            List<long> ban;
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
                incident.LastActivityUtc = Clock.UtcNow;
                lockNow = incident.Actions.HasFlag(RaidActions.Lock) && !incident.Locked;
                if (lockNow) incident.Locked = true;
                ban = incident.Actions.HasFlag(RaidActions.BanSuspects)
                    ? incident.Suspects.Where(s => incident.Handled.Add(s)).ToList()
                    : new List<long>();
                incident.Banned += ban.Count;
            }
            if (firstTime) RaidTelemetry.Activated(Snapshot(incident), by);
            Save(incident);
            if (lockNow) Bot.Dispatch(() => LockAsync(incident), "RaidLock");
            foreach (var u in ban) BanSuspect(chatId, u);
            Bot.Dispatch(() =>
            {
                UpdateNotice(chatId, force: true, tagAdmins: firstTime);
                return Task.CompletedTask;
            }, "RaidNotice");
        }

        // The group's own permissions are saved first, so they can be put back as they were.
        private static async Task LockAsync(RaidIncident incident)
        {
            var chatId = incident.ChatId;
            try
            {
                var chat = await Bot.Api.GetChat(chatId);
                lock (Lock) incident.SavedPermissions = chat.Permissions;
            }
            catch (Exception e)
            {
                // Locked all the same: stopping the raid comes first. Unlocking then gives the usual
                // permissions back, and the summary says to check them.
                LogHelper.Error($"Could not read the permissions of {chatId} before locking: {e.Message}");
            }
            Save(incident);
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
        }

        private static void Quarantine(long chatId, long userId)
        {
            var job = EnforcementJob.Mute(chatId, userId, Clock.UtcNow + Limits.QuarantineFor).For("Raid quarantine", null);
            job.Note.NewJoiner = true;
            Bot.Dispatch(async () => await EnforcementQueue.SubmitAsync(job), "RaidQuarantine");
            RaidTelemetry.Action(chatId, "quarantine", userId);
        }

        private static void BanSuspect(long chatId, long userId)
        {
            var job = EnforcementJob.Ban(chatId, userId, revokeMessages: true).For("Raid", null);
            Bot.Dispatch(async () => await EnforcementQueue.SubmitAsync(job), "RaidBan");
            RaidTelemetry.Action(chatId, "ban", userId);
        }

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
            }
            // One failing incident must not stop the others ending, or restart the loop.
            foreach (var i in quiet)
            {
                try { End(i.ChatId, null, quietly: i.Phase == RaidPhase.Alert); }
                catch (Exception e) { ErrorReporter.Report(e, $"Ending the raid in {i.ChatId}"); }
            }
            foreach (var chat in notices.Except(quiet.Select(i => i.ChatId))) UpdateNotice(chat, force: false, tagAdmins: false);
        }

        /// <summary>
        /// Ends the incident in a chat: puts the group's permissions back if we locked it and posts the
        /// summary. <paramref name="quietly"/> ends an alert nobody acted on without a word.
        /// </summary>
        internal static void End(long chatId, long? by, bool quietly = false)
        {
            RaidIncident incident;
            lock (Lock)
            {
                if (!Incidents.Remove(chatId, out incident)) return;
                Signs.Remove(chatId);
            }
            try
            {
                Redis.db.KeyDeleteAsync(IncidentKey(chatId)).GetAwaiter().GetResult();
                Redis.db.SetRemoveAsync(ActiveSetKey, chatId).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Forgetting the raid in {chatId}");
            }
            RaidTelemetry.Ended(Snapshot(incident), by, ignored: quietly && by.HasValue);
            if (incident.Locked)
            {
                var job = EnforcementJob.SetPermissions(chatId, incident.SavedPermissions ?? UsualPermissions);
                job.Note.Reason = "Raid over";
                Bot.Dispatch(async () => await EnforcementQueue.SubmitAsync(job), "RaidUnlock");
            }
            if (quietly || incident.Phase != RaidPhase.Active) return;
            Bot.Dispatch(() =>
            {
                var lang = Methods.GetGroupLanguage(chatId).Doc;
                var text = Methods.GetLocaleString(lang, "raidEnded", incident.Joined, incident.Muted, incident.Banned,
                    (int)Limits.QuarantineFor.TotalHours);
                var markup = incident.Quarantined.Count > 0 ? Buttons(lang, chatId, ("raidReleaseButton", "release")) : null;
                if (incident.NoticeMessageId is { } notice)
                {
                    // The notice's End button would do nothing now.
                    _ = Bot.Api.EditMessageReplyMarkup(chatId, notice, null);
                }
                Bot.Send(text, chatId, markup);
                return Task.CompletedTask;
            }, "RaidEnded");
        }

        /// <summary>What a group gets back when its own permissions could not be read before the lock.</summary>
        internal static readonly ChatPermissions UsualPermissions = new ChatPermissions
        {
            CanSendMessages = true, CanSendAudios = true, CanSendDocuments = true, CanSendPhotos = true, CanSendVideos = true,
            CanSendVideoNotes = true, CanSendVoiceNotes = true, CanSendPolls = true, CanSendOtherMessages = true,
            CanAddWebPagePreviews = true, CanInviteUsers = true
        };

        /// <summary>Unmutes everyone quarantined in the last incident there (the Release button).</summary>
        internal static void Release(long chatId, IEnumerable<long> users, long by)
        {
            foreach (var u in users)
                Bot.Dispatch(async () => await EnforcementQueue.SubmitAsync(EnforcementJob.Unmute(chatId, u).For("Raid over", by)), "RaidRelease");
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
            try
            {
                var lang = Methods.GetGroupLanguage(chatId).Doc;
                var text = Methods.GetLocaleString(lang, "raidActive", incident.Joined, incident.Muted, incident.Banned,
                    (int)Limits.QuarantineFor.TotalHours);
                if (incident.Locked) text += "\n" + Methods.GetLocaleString(lang, "raidLocked");
                if (tagAdmins) text += "\n" + AdminTags(chatId);
                var markup = Buttons(lang, chatId, ("raidEndButton", "end"));
                if (incident.NoticeMessageId is { } id && !tagAdmins)
                {
                    _ = Bot.Edit(chatId, id, text, markup);
                }
                else
                {
                    var sent = Bot.Send(text, chatId, markup);
                    if (sent != null) lock (Lock) incident.NoticeMessageId = sent.MessageId;
                }
                Save(incident);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Raid notice in {chatId}");
            }
        }

        // Every admin, tagged, so their phones go off.
        private static string AdminTags(long chatId)
        {
            try
            {
                var admins = Bot.Api.GetChatAdministrators(chatId).GetAwaiter().GetResult();
                return string.Join(" ", admins.Where(a => !a.User.IsBot)
                    .Select(a => $"<a href=\"tg://user?id={a.User.Id}\">{a.User.FirstName.FormatHTML()}</a>"));
            }
            catch (Exception e)
            {
                LogHelper.Error($"Could not list the admins of {chatId} to tag them: {e.Message}");
                return "";
            }
        }

        private static InlineKeyboardMarkup Buttons(XDocument lang, long chatId, params (string Key, string Action)[] buttons) =>
            new InlineKeyboardMarkup(buttons.Select(b =>
                new[] { InlineKeyboardButton.WithCallbackData(Methods.GetLocaleString(lang, b.Key), $"raid:{chatId}:{b.Action}") }));

        // An untrusted member posted nothing but a link: the admins decide.
        private static void ReportLink(MessageContext ctx)
        {
            var chatId = ctx.ChatId;
            var user = ctx.UserId;
            lock (Lock)
            {
                if (Incidents.TryGetValue(chatId, out var i) && i.Phase == RaidPhase.Active && i.Actions.HasFlag(RaidActions.BanSuspects))
                    return; // being banned already
                if (LinkReported.TryGetValue((chatId, user), out var at) && Clock.UtcNow - at < Limits.LinkReportEvery) return;
                LinkReported[(chatId, user)] = Clock.UtcNow;
            }
            if (ctx.Setting("Flagged").Equals("yes")) return; // the group turned reports off
            var message = ctx.Message;
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

        private static void Save(RaidIncident incident)
        {
            string json;
            lock (Lock) json = incident.ToJson();
            try
            {
                Redis.db.StringSetAsync(IncidentKey(incident.ChatId), json, null, false, When.Always, CommandFlags.None).GetAwaiter().GetResult();
                Redis.db.SetAddAsync(ActiveSetKey, incident.ChatId).GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Saving the raid in {incident.ChatId}");
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
                    lock (Lock) Incidents.TryAdd(chatId, incident);
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
                LinkReported.Clear();
                TrustSeen.Clear();
                Modes.Clear();
                Pending.Clear();
                PendingSpamKeys.Clear();
            }
            Limits = new RaidLimits();
        }

        /// <summary>Reloads the incidents from Redis, as a restart does. For tests.</summary>
        internal static Task ReloadForTests() => LoadAsync();
    }
}
