using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;

namespace Enforcer5.Enforcement
{
    /// <summary>
    /// Every ban, kick, mute, delete and permission change goes through here, so enforcement never
    /// fails because Telegram is busy:
    /// <list type="bullet">
    /// <item><b>Never dropped.</b> A job is in the Redis journal before its submitter hears back,
    /// and stays there until Telegram has carried it out or refused it for good. A 429 puts it back
    /// for the <c>retry_after</c>; a restart reloads it.</item>
    /// <item><b>Separate from messages.</b> Its own workers make only moderation calls, which the
    /// gateway never holds behind a message pause.</item>
    /// <item><b>Fair.</b> Chats take turns, at most one job running per chat, so a raided group cannot
    /// starve the others. Within a chat, closing the door goes first, then bans and kicks, then mutes,
    /// then deletes and unbans; jobs for the same person keep their order.</item>
    /// <item><b>On time.</b> A tempban or mute carried out late still ends when it was meant to.</item>
    /// <item><b>Recorded on success.</b> The sanction history is written when Telegram has carried the
    /// job out (<see cref="EnforcementExecutor.Record"/>).</item>
    /// </list>
    /// Callers that need an answer use <see cref="Run"/>: it waits up to <see cref="CallerWait"/>; a
    /// job still queued by then counts as success, since it will happen.
    /// </summary>
    internal static class EnforcementQueue
    {
        /// <summary>How long <see cref="Run"/> waits for Telegram before answering "queued".</summary>
        internal static readonly TimeSpan CallerWait = TimeSpan.FromSeconds(3);

        /// <summary>How long a job waits for the bot to get its rights back before it is dropped.</summary>
        internal static readonly TimeSpan RightsGrace = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan RightsRetry = TimeSpan.FromMinutes(1);
        private const int MaxTransientAttempts = 8;

        /// <summary>Jobs carried out at the same time, each in a different chat.</summary>
        internal static int MaxConcurrent = 16;

        private static readonly object Lock = new();
        private static readonly Dictionary<long, List<EnforcementJob>> ByChat = new();
        private static readonly Dictionary<string, EnforcementJob> ByKey = new();
        private static readonly HashSet<long> Running = new();
        // A chat Telegram answered 429 for moderation: nothing there is tried before this. Also used
        // while the bot lacks its rights there: one probe a minute, not one call per job.
        private static readonly Dictionary<long, DateTime> ChatNotBefore = new();
        // Chats where Telegram said the bot lacks the rights, since when.
        private static readonly Dictionary<long, DateTime> NoRightsSince = new();
        // (chat, user) Telegram said is an admin there, until when that is believed.
        private static readonly Dictionary<(long Chat, long User), DateTime> KnownAdmins = new();
        private static readonly TimeSpan KnownAdminFor = TimeSpan.FromMinutes(10);
        // (chat, user) the queue muted: until when (null: for good), and until when that is believed.
        // A raid mutes the same joiner up to three times (Mute On Join, quarantine, as a suspect);
        // a mute that one already covers is not asked of Telegram again.
        private static readonly Dictionary<(long Chat, long User), (DateTime? Until, DateTime Believed)> RecentlyMuted = new();
        internal static readonly TimeSpan RecentlyMutedFor = TimeSpan.FromMinutes(10);

        /// <summary>Most messages Telegram deletes in one deleteMessages call.</summary>
        internal const int MaxDeleteBatch = 100;
        private static long _sequence;
        private static long _nextChatTurn;
        private static CancellationTokenSource _wake;
        private static CancellationTokenSource _stop;
        private static TaskCompletionSource<bool> _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static IEnforcementJournal Journal => Repositories.EnforcementJournal;

        /// <summary>Starts the worker: loads the journal, then carries out jobs until <paramref name="stop"/>.</summary>
        internal static void Start(CancellationToken stop)
        {
            lock (Lock)
            {
                _stop = CancellationTokenSource.CreateLinkedTokenSource(stop);
            }
            var token = _stop.Token;
            Bot.StartService(ct => RunAsync(ct), token, "EnforcementQueue");
        }

        /// <summary>Jobs waiting or running, for tests and /status.</summary>
        internal static int Count
        {
            get { lock (Lock) return ByChat.Values.Sum(l => l.Count); }
        }

        /// <summary>Jobs waiting or running in one chat.</summary>
        internal static int CountIn(long chatId)
        {
            lock (Lock) return ByChat.TryGetValue(chatId, out var l) ? l.Count : 0;
        }

        /// <summary>
        /// The permissions the chat will have once its queued permission changes are done (the latest
        /// one submitted); null when none is waiting. What Telegram says now is out of date until then.
        /// </summary>
        internal static Telegram.Bot.Types.ChatPermissions PendingPermissions(long chatId)
        {
            lock (Lock)
                return ByChat.TryGetValue(chatId, out var l)
                    ? l.Where(j => j.Kind == EnforcementKind.SetChatPermissions).OrderByDescending(j => j.Sequence).FirstOrDefault()?.Permissions
                    : null;
        }

        /// <summary>
        /// Submits a job and waits up to <see cref="CallerWait"/> for it. Blocking, for the
        /// synchronous command code. Never throws.
        /// </summary>
        internal static EnforcementResult Run(EnforcementJob job)
        {
            try
            {
                var done = SubmitAsync(job).GetAwaiter().GetResult();
                // Real enforcement is what the bot is for: the command that asked for it costs
                // nothing. Given back as soon as it is queued, so an admin banning raiders quickly
                // is not slowed while Telegram catches up. The same job already queued, or one
                // refused at once, was a wasted call and is not.
                var isNew = job.Completion != null;
                if (isNew && !(done.IsCompleted && !done.Result.Succeeded)) CommandGate.RefundCurrent();
                var result = Clock.Wait(done, CallerWait) ? done.Result : EnforcementResult.Queued;
                if (!result.Succeeded && result.Refusal != Refusal.BotNotAdmin) CommandGate.ChargeBackCurrent();
                return result;
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Queueing {job}");
                return EnforcementResult.Refuse(Refusal.Other, e);
            }
        }

        /// <summary>
        /// Queues a job; the task ends when it is done or refused for good. The same job already
        /// waiting (same kind, chat and target) is not queued twice: both get its result.
        /// </summary>
        internal static async Task<Task<EnforcementResult>> SubmitAsync(EnforcementJob job)
        {
            var now = Clock.UtcNow;
            EnforcementJob existing = null;
            var refusedForNow = false;
            lock (Lock)
            {
                // Telegram already said this person is an admin there: asking again is a wasted call.
                if (job.TargetId != 0 && job.Kind is not (EnforcementKind.Delete or EnforcementKind.SetChatPermissions) &&
                    KnownAdmins.TryGetValue((job.ChatId, job.TargetId), out var adminUntil))
                {
                    if (adminUntil > now) return Task.FromResult(EnforcementResult.Refuse(Refusal.TargetIsAdmin));
                    KnownAdmins.Remove((job.ChatId, job.TargetId));
                }
                // The bot lacks its rights there: queued all the same, to be carried out once an
                // admin fixes them, but the caller hears it at once.
                refusedForNow = NoRightsSince.ContainsKey(job.ChatId);

                if (job.Kind == EnforcementKind.Mute && job.Note.ActorId == null)
                {
                    // The bot's own mute (on joining, raid quarantine, a raid suspect), never an admin's,
                    // who may mean to change it. Muted already, for at least as long: done. Waiting to be
                    // muted for that long: the same job.
                    if (RecentlyMuted.TryGetValue((job.ChatId, job.TargetId), out var muted) && muted.Believed > now &&
                        Covers(muted.Until, job.UntilUtc) && IsLatestDone(job.ChatId, job.TargetId))
                        return Task.FromResult(EnforcementResult.Done);
                    if (LatestFor(job.ChatId, job.TargetId) is { Kind: EnforcementKind.Mute } waiting && Covers(waiting.UntilUtc, job.UntilUtc))
                        return waiting.Completion?.Task ?? Task.FromResult(EnforcementResult.Queued);
                }
                if (job.TargetId != 0 && job.Kind is not (EnforcementKind.Mute or EnforcementKind.Delete or EnforcementKind.SetChatPermissions))
                {
                    RecentlyMuted.Remove((job.ChatId, job.TargetId));
                }

                var key = job.DedupeKey;
                // Only the same job as the person's latest: after "ban, unban", a new ban is a new job.
                if (key != null && ByKey.TryGetValue(key, out existing) && IsLatestFor(existing)) { }
                else
                {
                    existing = null;
                    job.Completion = new TaskCompletionSource<EnforcementResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    job.Context ??= ErrorReporter.Current;
                    job.CreatedUtc = now;
                    job.DueUtc = job.NotBeforeUtc is { } notBefore && notBefore > now ? notBefore : now;
                    job.Sequence = ++_sequence;
                    job.Note.TriggerEventId ??= job.Trigger?.EventId;
                    job.Note.ReportId ??= job.Report?.ReportId;
                    Add(job);
                }
            }
            if (existing != null)
                return existing.Completion?.Task ?? Task.FromResult(EnforcementResult.Queued);

            try
            {
                await Journal.SaveAsync(job);
            }
            catch (Exception e)
            {
                // Still carried out from memory; only a restart before then would lose it.
                ErrorReporter.Report(e, $"Journaling {job}");
            }
            if (refusedForNow) CompleteOnce(job, EnforcementResult.Refuse(Refusal.BotNotAdmin));
            Wake();
            return job.Completion.Task;
        }

        // A mute until <paramref name="known"/> covers one until <paramref name="wanted"/> (null: for
        // good), give or take ten minutes, so a quarantine and a suspect's mute a moment later are one.
        private static bool Covers(DateTime? known, DateTime? wanted) =>
            known == null || wanted != null && known.Value >= wanted.Value - TimeSpan.FromMinutes(10);

        // With the lock held. The person's latest job waiting in the chat, if any.
        private static EnforcementJob LatestFor(long chatId, long targetId) =>
            ByChat.TryGetValue(chatId, out var list)
                ? list.Where(j => j.TargetId == targetId).OrderByDescending(j => j.Sequence).FirstOrDefault()
                : null;

        // With the lock held. Nothing waits for the person in the chat.
        private static bool IsLatestDone(long chatId, long targetId) => LatestFor(chatId, targetId) == null;

        /// <summary>
        /// Telegram says the person is no longer muted (an admin lifted it by hand): a mute asked for
        /// later is carried out, not taken as done.
        /// </summary>
        internal static void ForgetMute(long chatId, long userId)
        {
            lock (Lock) RecentlyMuted.Remove((chatId, userId));
        }

        // With the lock held.
        private static bool IsLatestFor(EnforcementJob job) =>
            ByChat.TryGetValue(job.ChatId, out var list) &&
            !list.Any(o => o != job && o.TargetId == job.TargetId && o.Sequence > job.Sequence);

        // With the lock held.
        private static void Add(EnforcementJob job)
        {
            if (!ByChat.TryGetValue(job.ChatId, out var list)) ByChat[job.ChatId] = list = new List<EnforcementJob>();
            list.Add(job);
            if (job.DedupeKey != null) ByKey[job.DedupeKey] = job;
        }

        // With the lock held.
        private static void Remove(EnforcementJob job)
        {
            if (ByChat.TryGetValue(job.ChatId, out var list))
            {
                list.Remove(job);
                if (list.Count == 0) ByChat.Remove(job.ChatId);
            }
            if (job.DedupeKey != null && ByKey.TryGetValue(job.DedupeKey, out var keyed) && keyed == job)
                ByKey.Remove(job.DedupeKey);
        }

        private static void Wake()
        {
            lock (Lock) _wake?.Cancel();
        }

        private static async Task RunAsync(CancellationToken stop)
        {
            await LoadJournalAsync();
            // Started before Telegram is connected, so the journal is loaded early; nothing can be
            // carried out until it is.
            while (Bot.Api == null && !stop.IsCancellationRequested)
                await Clock.WaitAsync(TimeSpan.FromSeconds(1), stop);
            while (!stop.IsCancellationRequested)
            {
                var wake = new CancellationTokenSource();
                List<EnforcementJob> ready;
                TimeSpan sleep;
                lock (Lock)
                {
                    _wake = wake;
                    ready = PickReady(Clock.UtcNow, out sleep);
                    foreach (var job in ready) Running.Add(job.ChatId);
                }
                foreach (var job in ready)
                {
                    var picked = job;
                    Bot.Dispatch(() => CarryOutAsync(picked), "Enforcement");
                }
                using (stop.Register(() => wake.Cancel()))
                {
                    await Clock.WaitAsync(sleep, wake.Token);
                }
                lock (Lock)
                {
                    if (_wake == wake) _wake = null;
                }
                wake.Dispose();
            }
        }

        private static async Task LoadJournalAsync()
        {
            try
            {
                var jobs = await Journal.LoadAsync();
                lock (Lock)
                {
                    foreach (var job in jobs.OrderBy(j => j.Sequence).ThenBy(j => j.CreatedUtc))
                    {
                        if (ByChat.Values.Any(l => l.Any(j => j.Id == job.Id))) continue;
                        job.Sequence = ++_sequence;
                        Add(job);
                    }
                }
                if (jobs.Count > 0) LogHelper.Info($"Enforcement queue: {jobs.Count} jobs reloaded from the journal");
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, "Loading the enforcement journal");
            }
            finally
            {
                _loaded.TrySetResult(true);
            }
        }

        // With the lock held. The next job of each chat whose turn it is, chats in rotation, and how
        // long to sleep when nothing is ready.
        private static List<EnforcementJob> PickReady(DateTime now, out TimeSpan sleep)
        {
            var ready = new List<EnforcementJob>();
            var nextDue = DateTime.MaxValue;
            var chats = ByChat.Keys.OrderBy(c => c).ToList();
            if (chats.Count > 0)
            {
                // Start the rotation where it left off, so the same chats are not always first.
                var start = (int)(_nextChatTurn++ % chats.Count);
                chats = chats.Skip(start).Concat(chats.Take(start)).ToList();
            }
            foreach (var chat in chats)
            {
                if (Running.Contains(chat)) continue;
                if (ChatNotBefore.TryGetValue(chat, out var notBefore))
                {
                    if (notBefore > now)
                    {
                        if (notBefore < nextDue) nextDue = notBefore;
                        continue;
                    }
                    ChatNotBefore.Remove(chat);
                }
                var job = NextIn(ByChat[chat], now, ref nextDue);
                if (job == null) continue;
                if (Running.Count + ready.Count >= MaxConcurrent) break;
                if (job.Kind == EnforcementKind.Delete) Batch(job, ByChat[chat], now);
                ready.Add(job);
            }
            sleep = nextDue == DateTime.MaxValue ? TimeSpan.FromMinutes(5) : nextDue - now;
            if (sleep < TimeSpan.Zero) sleep = TimeSpan.Zero;
            if (ready.Count > 0 && sleep > TimeSpan.FromSeconds(1)) sleep = TimeSpan.FromSeconds(1);
            return ready;
        }

        // With the lock held. Folds the chat's other due deletes into this one: up to 100 messages
        // go in one deleteMessages call. The others ride along and finish with it.
        private static void Batch(EnforcementJob lead, List<EnforcementJob> jobs, DateTime now)
        {
            // A new list, never the old one changed: the job may be being written to the journal
            // on another thread right now (Submit journals outside the lock).
            var merged = new List<int>(lead.MessageIds ?? new List<int>());
            foreach (var other in jobs.Where(j => j != lead && j.Kind == EnforcementKind.Delete && j.DueUtc <= now).ToList())
            {
                var ids = other.MessageIds ?? new List<int>();
                if (merged.Count + ids.Count > MaxDeleteBatch) break;
                merged.AddRange(ids.Where(id => !merged.Contains(id)));
                lead.Riders.Add(other);
                Remove(other);
            }
            lead.MessageIds = merged;
        }

        // The chat's next job: by priority, then first come, first served, except that a person's
        // jobs keep their order (an unban must not overtake the ban it lifts).
        private static EnforcementJob NextIn(List<EnforcementJob> jobs, DateTime now, ref DateTime nextDue)
        {
            EnforcementJob best = null;
            foreach (var job in jobs)
            {
                if (job.TargetId != 0 && jobs.Any(o => o != job && o.TargetId == job.TargetId && o.Sequence < job.Sequence))
                    continue;
                if (job.DueUtc > now)
                {
                    if (job.DueUtc < nextDue) nextDue = job.DueUtc;
                    continue;
                }
                if (best == null || job.Priority < best.Priority || job.Priority == best.Priority && job.Sequence < best.Sequence)
                    best = job;
            }
            return best;
        }

        // Bumped by a (simulated) crash, so work picked before it does not run after it.
        private static int _generation;

        private static async Task CarryOutAsync(EnforcementJob job)
        {
            var generation = Volatile.Read(ref _generation);
            if (job.Context != null) ErrorReporter.Current = job.Context;
            Attempt attempt;
            try
            {
                attempt = await EnforcementExecutor.ExecuteAsync(job);
            }
            catch (Exception e)
            {
                attempt = Attempt.Transient(e);
            }

            // The process "died" meanwhile: what it was doing is gone with it; the journal has the job.
            if (generation != Volatile.Read(ref _generation)) return;
            var now = Clock.UtcNow;
            EnforcementResult? result = null;
            var keep = false;
            switch (attempt.Kind)
            {
                case AttemptKind.Success:
                    EnforcementExecutor.Record(job);
                    result = EnforcementResult.Done;
                    lock (Lock)
                    {
                        NoRightsSince.Remove(job.ChatId);
                        if (job.Kind == EnforcementKind.Mute) RecentlyMuted[(job.ChatId, job.TargetId)] = (job.UntilUtc, now + RecentlyMutedFor);
                    }
                    break;
                case AttemptKind.RateLimited:
                    job.DueUtc = now + attempt.RetryIn;
                    lock (Lock) ChatNotBefore[job.ChatId] = job.DueUtc;
                    keep = true;
                    break;
                case AttemptKind.Transient:
                    job.Attempts++;
                    if (job.Attempts < MaxTransientAttempts)
                    {
                        job.DueUtc = now + TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, job.Attempts)));
                        keep = true;
                    }
                    else
                    {
                        ErrorReporter.Report(attempt.Error, $"{job} kept failing, given up");
                        result = EnforcementResult.Refuse(Refusal.Other, attempt.Error);
                    }
                    break;
                case AttemptKind.Refused when attempt.Refusal == Refusal.BotNotAdmin:
                    // Admins often fix the bot's rights within minutes of being told; keep the job
                    // that long. The submitter hears now, so the group is told at once.
                    job.RefusedSinceUtc ??= now;
                    LogHelper.Error($"{job}: the bot lacks the rights ({attempt.Error?.Message})");
                    lock (Lock)
                    {
                        if (!NoRightsSince.ContainsKey(job.ChatId)) NoRightsSince[job.ChatId] = now;
                        ChatNotBefore[job.ChatId] = now + RightsRetry;
                    }
                    if (now - job.RefusedSinceUtc.Value < RightsGrace)
                    {
                        job.DueUtc = now + RightsRetry;
                        keep = true;
                    }
                    CompleteOnce(job, EnforcementResult.Refuse(Refusal.BotNotAdmin, attempt.Error));
                    break;
                case AttemptKind.Refused when attempt.Refusal == Refusal.TargetIsAdmin:
                    lock (Lock) KnownAdmins[(job.ChatId, job.TargetId)] = now + KnownAdminFor;
                    LogHelper.Info($"{job}: the target is an admin");
                    result = EnforcementResult.Refuse(Refusal.TargetIsAdmin, attempt.Error);
                    break;
                case AttemptKind.Refused:
                    if (attempt.Refusal == Refusal.Other)
                        ErrorReporter.Report(attempt.Error, $"Telegram refused {job}");
                    else
                        LogHelper.Info($"{job}: {attempt.Refusal} ({attempt.Error?.Message})");
                    result = EnforcementResult.Refuse(attempt.Refusal, attempt.Error);
                    break;
            }

            try
            {
                if (keep) await Journal.SaveAsync(job);
                else await Journal.RemoveAsync(job.Id);
                // Deletes that rode along are in the lead job now (saved above when it stays).
                foreach (var rider in job.Riders) await Journal.RemoveAsync(rider.Id);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Updating the journal for {job}");
            }

            lock (Lock)
            {
                if (!keep) Remove(job);
                Running.Remove(job.ChatId);
            }
            if (result.HasValue) CompleteOnce(job, result.Value);
            if (!keep) job.Riders.Clear();
            Wake();
        }

        private static void CompleteOnce(EnforcementJob job, EnforcementResult result)
        {
            job.Completion?.TrySetResult(result);
            foreach (var rider in job.Riders) rider.Completion?.TrySetResult(result);
        }

        /// <summary>The bot was told it lacks its rights in <paramref name="chatId"/> and has not succeeded there since.</summary>
        internal static bool LacksRights(long chatId)
        {
            lock (Lock) return NoRightsSince.ContainsKey(chatId);
        }

        /// <summary>
        /// Stops the worker and forgets everything in memory, as a crash would; the journal stays.
        /// For the harness, which then calls <see cref="Start"/> again.
        /// </summary>
        internal static void CrashForTests()
        {
            Interlocked.Increment(ref _generation);
            lock (Lock)
            {
                _stop?.Cancel();
                _wake?.Cancel();
                foreach (var job in ByChat.Values.SelectMany(l => l))
                    job.Completion?.TrySetResult(EnforcementResult.Queued);
                ByChat.Clear();
                ByKey.Clear();
                Running.Clear();
                ChatNotBefore.Clear();
                NoRightsSince.Clear();
                KnownAdmins.Clear();
                RecentlyMuted.Clear();
                _loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        /// <summary>Stops the worker and forgets everything, for tests.</summary>
        internal static void ResetForTests() => CrashForTests();
    }
}
