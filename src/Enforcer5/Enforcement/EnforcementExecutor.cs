using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Models;
using StackExchange.Redis;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;

namespace Enforcer5.Enforcement
{
    /// <summary>What one attempt at a job came to.</summary>
    internal readonly record struct Attempt(AttemptKind Kind, TimeSpan RetryIn = default, Refusal Refusal = Refusal.None, Exception Error = null)
    {
        internal static Attempt Success => new(AttemptKind.Success);
        internal static Attempt RateLimited(TimeSpan retryIn) => new(AttemptKind.RateLimited, retryIn);
        internal static Attempt Refused(Refusal why, Exception error) => new(AttemptKind.Refused, Refusal: why, Error: error);
        internal static Attempt Transient(Exception error) => new(AttemptKind.Transient, Error: error);
    }

    internal enum AttemptKind
    {
        Success,
        /// <summary>429: try again after <see cref="Attempt.RetryIn"/>.</summary>
        RateLimited,
        /// <summary>Telegram said no, for a reason that will not change by asking again.</summary>
        Refused,
        /// <summary>Network trouble or a timeout: try again soon.</summary>
        Transient
    }

    /// <summary>
    /// Carries out one job against Telegram (through the gateway, so it is paced with everything
    /// else), and records it once done. Kept apart from the queue so each piece can be tested alone.
    /// </summary>
    internal static class EnforcementExecutor
    {
        /// <summary>Everything off: a muted member, or a locked chat.</summary>
        internal static readonly ChatPermissions NothingAllowed = new ChatPermissions
        {
            CanSendMessages = false,
            CanSendAudios = false,
            CanSendDocuments = false,
            CanSendPhotos = false,
            CanSendVideos = false,
            CanSendVideoNotes = false,
            CanSendVoiceNotes = false,
            CanSendPolls = false,
            CanSendOtherMessages = false,
            CanAddWebPagePreviews = false,
            CanChangeInfo = false,
            CanInviteUsers = false,
            CanPinMessages = false,
            CanManageTopics = false
        };

        /// <summary>
        /// Everything allowed. Telegram reads this as "lift the restriction": the user becomes a
        /// normal member again and follows the group's own permissions from then on, including later
        /// changes to them.
        /// </summary>
        internal static readonly ChatPermissions EverythingAllowed = new ChatPermissions
        {
            CanSendMessages = true,
            CanSendAudios = true,
            CanSendDocuments = true,
            CanSendPhotos = true,
            CanSendVideos = true,
            CanSendVideoNotes = true,
            CanSendVoiceNotes = true,
            CanSendPolls = true,
            CanSendOtherMessages = true,
            CanAddWebPagePreviews = true,
            CanChangeInfo = true,
            CanInviteUsers = true,
            CanPinMessages = true,
            CanManageTopics = true
        };

        // Telegram treats an until date less than 30 seconds away as "forever". A tempban or
        // tempmute carried out late is shortened to the time it has left, but never below this.
        private static readonly TimeSpan ShortestUntil = TimeSpan.FromSeconds(35);

        internal static async Task<Attempt> ExecuteAsync(EnforcementJob job)
        {
            try
            {
                var api = Bot.Api;
                switch (job.Kind)
                {
                    case EnforcementKind.Ban:
                        await api.BanChatMember(job.ChatId, job.TargetId, Until(job), job.RevokeMessages);
                        break;
                    case EnforcementKind.Kick:
                        // A ban lifted at once: two calls. If the second is rate limited, the job
                        // resumes at it rather than banning again.
                        if (job.Stage == 0)
                        {
                            await api.BanChatMember(job.ChatId, job.TargetId, revokeMessages: job.RevokeMessages);
                            job.Stage = 1;
                        }
                        await api.UnbanChatMember(job.ChatId, job.TargetId, onlyIfBanned: true);
                        break;
                    case EnforcementKind.Unban:
                        // only_if_banned: without it Telegram removes a current member, so /unban of
                        // someone still in the group kicked them.
                        await api.UnbanChatMember(job.ChatId, job.TargetId, onlyIfBanned: true);
                        break;
                    case EnforcementKind.Mute:
                        await api.RestrictChatMember(job.ChatId, job.TargetId, NothingAllowed, untilDate: Until(job));
                        break;
                    case EnforcementKind.Unmute:
                        await api.RestrictChatMember(job.ChatId, job.TargetId, EverythingAllowed);
                        break;
                    case EnforcementKind.BanSenderChat:
                        await api.BanChatSenderChat(job.ChatId, job.TargetId);
                        break;
                    case EnforcementKind.UnbanSenderChat:
                        await api.UnbanChatSenderChat(job.ChatId, job.TargetId);
                        break;
                    case EnforcementKind.SetChatPermissions:
                        await api.SetChatPermissions(job.ChatId, job.Permissions ?? NothingAllowed);
                        break;
                    case EnforcementKind.Delete:
                        var ids = job.MessageIds ?? new System.Collections.Generic.List<int>();
                        if (ids.Count == 1) await api.DeleteMessage(job.ChatId, ids[0]);
                        else if (ids.Count > 1) await api.DeleteMessages(job.ChatId, ids);
                        break;
                }
                return Attempt.Success;
            }
            catch (Exception e)
            {
                return Classify(job, e);
            }
        }

        private static DateTime? Until(EnforcementJob job)
        {
            if (!job.UntilUtc.HasValue) return null;
            var earliest = Clock.UtcNow + ShortestUntil;
            return job.UntilUtc.Value < earliest ? earliest : job.UntilUtc.Value;
        }

        internal static Attempt Classify(EnforcementJob job, Exception e)
        {
            var api = Bot.AsApiError(e) ?? (ErrorReporter.Unwrap(e) as ApiRequestException);
            if (api == null)
            {
                var root = ErrorReporter.Unwrap(e);
                return root is HttpRequestException or TaskCanceledException or TimeoutException or RequestException
                    ? Attempt.Transient(e)
                    : Attempt.Refused(Refusal.Other, e);
            }
            if (api.ErrorCode == 429)
                return Attempt.RateLimited(TimeSpan.FromSeconds(Math.Max(1, api.Parameters?.RetryAfter ?? 5)));
            if (api.ErrorCode >= 500)
                return Attempt.Transient(e);

            var text = api.Message?.ToLowerInvariant() ?? "";
            if (job.Kind == EnforcementKind.Delete)
            {
                // Already gone, or too old for bots to delete: nothing left to do either way.
                if (text.Contains("message to delete not found") || text.Contains("message can't be deleted") &&
                    !text.Contains("rights"))
                    return Attempt.Success;
            }
            if (api.ErrorCode == 403 || text.Contains("chat not found") || text.Contains("bot was kicked") ||
                text.Contains("bot is not a member"))
                return Attempt.Refused(Refusal.BotNotAdmin, e);
            switch (RestrictFailures.Classify(api.Message))
            {
                case RestrictFailure.BotNotAdmin:
                    return Attempt.Refused(Refusal.BotNotAdmin, e);
                case RestrictFailure.TargetIsAdmin:
                    return Attempt.Refused(Refusal.TargetIsAdmin, e);
            }
            if (text.Contains("can't remove chat owner") || text.Contains("user is an administrator") ||
                text.Contains("can't restrict self"))
                return Attempt.Refused(Refusal.TargetIsAdmin, e);
            if (text.Contains("user not found") || text.Contains("member not found") || text.Contains("participant_id_invalid") ||
                text.Contains("user_id_invalid") || text.Contains("user_not_participant"))
                return Attempt.Refused(Refusal.NotFound, e);
            return Attempt.Refused(Refusal.Other, e);
        }

        /// <summary>
        /// Records a job Telegram carried out: the counters, the mute lists and the sanction history,
        /// as the code calling Telegram directly used to. Never throws.
        /// </summary>
        internal static void Record(EnforcementJob job)
        {
            try
            {
                var note = job.Note ?? new EnforcementNote();
                var trigger = job.Trigger ?? (note.TriggerEventId.HasValue ? new WarningRef { EventId = note.TriggerEventId } : null);
                var report = job.Report ?? (note.ReportId.HasValue ? new ReportRef { ReportId = note.ReportId } : null);
                switch (job.Kind)
                {
                    case EnforcementKind.Ban:
                        Count("ban");
                        if (note.Record)
                            BanHistory.Banned(job.ChatId, job.TargetId, job.UntilUtc.HasValue ? SanctionKind.Tempban : SanctionKind.Ban,
                                note.Reason, note.ActorId, job.UntilUtc, trigger, report);
                        break;
                    case EnforcementKind.Kick:
                        Count("kick");
                        if (note.Record) BanHistory.Kicked(job.ChatId, job.TargetId, note.Reason, note.ActorId, trigger, report);
                        break;
                    case EnforcementKind.Unban:
                        Redis.db.SetRemoveAsync($"chat:{job.ChatId}:bannedlist", job.TargetId);
                        BanHistory.Unbanned(job.ChatId, job.TargetId, note.End, note.ActorId);
                        break;
                    case EnforcementKind.Mute:
                        Remember(job.UntilUtc.HasValue
                                ? Repositories.Mutes.MarkTempMutedAsync(job.ChatId, job.TargetId, job.UntilUtc.Value)
                                : note.NewJoiner
                                    ? Repositories.Mutes.MarkJoinerMutedAsync(job.ChatId, job.TargetId)
                                    : Repositories.Mutes.MarkMutedAsync(job.ChatId, job.TargetId),
                            job);
                        if (note.Record)
                            BanHistory.Muted(job.ChatId, job.TargetId, note.NewJoiner ? note.Reason ?? "Mute On Join" : note.Reason,
                                note.ActorId, job.UntilUtc);
                        break;
                    case EnforcementKind.Unmute:
                        Remember(Repositories.Mutes.ClearAsync(job.ChatId, job.TargetId), job);
                        if (note.Record) BanHistory.Unmuted(job.ChatId, job.TargetId, note.ActorId);
                        break;
                }
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Recording {job}");
            }
        }

        private static void Count(string what) =>
            Redis.db.HashIncrementAsync("bot:general", what, 1);

        // The action itself already succeeded, so failing to record it is reported, not retried.
        private static void Remember(Task record, EnforcementJob job)
        {
            try
            {
                record.GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Recording {job}");
            }
        }
    }
}
