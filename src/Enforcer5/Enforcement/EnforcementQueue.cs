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
        // A chat Telegram answered 429 for moderation: nothing there is tried before this.
        private static readonly Dictionary<long, DateTime> ChatNotBefore = new();
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
        /// Submits a job and waits up to <see cref="CallerWait"/> for it. Blocking, for the
        /// synchronous command code. Never throws.
        /// </summary>
        internal static EnforcementResult Run(EnforcementJob job)
        {
            try
            {
                var done = SubmitAsync(job).GetAwaiter().GetResult();
                return Clock.Wait(done, CallerWait) ? done.Result : EnforcementResult.Queued;
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
            lock (Lock)
            {
                var key = job.DedupeKey;
                // Only the same job as the person's latest: after "ban, unban", a new ban is a new job.
                if (key != null && ByKey.TryGetValue(key, out existing) && IsLatestFor(existing)) { }
                else
                {
                    existing = null;
                    job.Completion = new TaskCompletionSource<EnforcementResult>(TaskCreationOptions.RunContinuationsAsynchronously);
                    job.Context ??= ErrorReporter.Current;
                    job.CreatedUtc = now;
                    job.DueUtc = now;
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
            Wake();
            return job.Completion.Task;
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
                ready.Add(job);
            }
            sleep = nextDue == DateTime.MaxValue ? TimeSpan.FromMinutes(5) : nextDue - now;
            if (sleep < TimeSpan.Zero) sleep = TimeSpan.Zero;
            if (ready.Count > 0 && sleep > TimeSpan.FromSeconds(1)) sleep = TimeSpan.FromSeconds(1);
            return ready;
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

        private static async Task CarryOutAsync(EnforcementJob job)
        {
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

            var now = Clock.UtcNow;
            EnforcementResult? result = null;
            var keep = false;
            switch (attempt.Kind)
            {
                case AttemptKind.Success:
                    EnforcementExecutor.Record(job);
                    result = EnforcementResult.Done;
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
                    if (now - job.RefusedSinceUtc.Value < RightsGrace)
                    {
                        job.DueUtc = now + RightsRetry;
                        keep = true;
                    }
                    CompleteOnce(job, EnforcementResult.Refuse(Refusal.BotNotAdmin, attempt.Error));
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
            Wake();
        }

        private static void CompleteOnce(EnforcementJob job, EnforcementResult result) =>
            job.Completion?.TrySetResult(result);

        /// <summary>
        /// Stops the worker and forgets everything in memory, as a crash would; the journal stays.
        /// For the harness, which then calls <see cref="Start"/> again.
        /// </summary>
        internal static void CrashForTests()
        {
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
                _loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        /// <summary>Stops the worker and forgets everything, for tests.</summary>
        internal static void ResetForTests() => CrashForTests();
    }
}
