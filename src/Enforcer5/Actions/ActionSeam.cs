using System;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Enforcement;
using Enforcer5.Helpers;

namespace Enforcer5.Actions
{
    /// <summary>
    /// The one way to act on a member (<c>docs/action-seam.md</c>). The request is carried out through
    /// <see cref="EnforcementQueue"/> (warns by <see cref="WarnActions"/>), then what it owns is recorded (<see cref="ActionEffects"/>: the tempban
    /// schedule, the ban list, the ban record). The steps before it (authorise, guards, hooks) arrive later.
    /// </summary>
    internal static class ActionSeam
    {
        /// <summary>Every action carried out, with its result. For tests; the hooks of step F replace it.</summary>
        internal static event Action<ActionResult> Ran;

        /// <summary>
        /// Carries the action out and waits for it as <see cref="EnforcementQueue.Run"/> does (up to
        /// <see cref="EnforcementQueue.CallerWait"/>, then queued). Never throws.
        /// </summary>
        internal static ActionResult Run(ActionRequest request)
        {
            ActionResult result;
            try
            {
                result = WarnActions.Handles(request.Kind)
                    ? WarnActions.Execute(request)
                    : new ActionResult(request, EnforcementQueue.Run(ToJob(request)));
            }
            catch (Exception e)
            {
                // A request the seam cannot carry out (a tempban without an end), or Redis failing a warn.
                ErrorReporter.Report(e, $"Carrying out the {request.Kind} of {request.TargetId} in {request.ChatId}");
                result = new ActionResult(request, EnforcementResult.Refuse(Refusal.Other, e));
            }
            return Finish(result);
        }

        /// <summary>
        /// Queues the action without waiting for Telegram, for the bot's defences (raid mode), which act on
        /// many members at once and must never wait on one. Awaiting it waits only until the job is
        /// journaled; the inner task ends when Telegram has done it or refused, and what the action owns is
        /// recorded then. Never throws. Warn kinds are not queued: use <see cref="Run"/>.
        /// </summary>
        internal static async Task<Task<ActionResult>> SubmitAsync(ActionRequest request)
        {
            Task<EnforcementResult> done;
            try
            {
                done = await EnforcementQueue.SubmitAsync(ToJob(request));
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Queueing the {request.Kind} of {request.TargetId} in {request.ChatId}");
                return Task.FromResult(Finish(new ActionResult(request, EnforcementResult.Refuse(Refusal.Other, e))));
            }
            if (done.IsCompleted) return Task.FromResult(Finish(new ActionResult(request, Outcome(done))));
            return done.ContinueWith(t => Finish(new ActionResult(request, Outcome(t))), TaskScheduler.Default);
        }

        private static EnforcementResult Outcome(Task<EnforcementResult> done) =>
            done.IsCompletedSuccessfully ? done.Result : EnforcementResult.Refuse(Refusal.Other, done.Exception);

        // What every action ends with: its effects recorded, then the observers told.
        private static ActionResult Finish(ActionResult result)
        {
            ActionEffects.Apply(result);
            try
            {
                Ran?.Invoke(result);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Telling observers about the {result.Applied.Kind} of {result.Applied.TargetId} in {result.Applied.ChatId}");
            }
            return result;
        }

        /// <summary>The enforcement job that carries <paramref name="request"/> out.</summary>
        internal static EnforcementJob ToJob(ActionRequest request)
        {
            var job = request.Kind switch
            {
                ActionKind.Ban => EnforcementJob.Ban(request.ChatId, request.TargetId, revokeMessages: request.RevokeMessages),
                ActionKind.TempBan => EnforcementJob.Ban(request.ChatId, request.TargetId, Required(request)),
                ActionKind.Unban => EnforcementJob.Unban(request.ChatId, request.TargetId,
                    request.Source == ActionSource.Expiry ? BanEnd.Expired : BanEnd.Unbanned),
                ActionKind.Kick => EnforcementJob.Kick(request.ChatId, request.TargetId),
                ActionKind.Mute => EnforcementJob.Mute(request.ChatId, request.TargetId),
                ActionKind.TempMute => EnforcementJob.Mute(request.ChatId, request.TargetId, Required(request)),
                ActionKind.Unmute => EnforcementJob.Unmute(request.ChatId, request.TargetId),
                ActionKind.Delete => EnforcementJob.Delete(request.ChatId, request.MessageId),
                ActionKind.BanChannel => EnforcementJob.BanSenderChat(request.ChatId, request.TargetId),
                ActionKind.UnbanChannel => EnforcementJob.UnbanSenderChat(request.ChatId, request.TargetId),
                _ => throw new ArgumentOutOfRangeException(nameof(request), request.Kind, "Not an action the seam carries out yet")
            };
            job.For(request.Reason, request.ActorId, request.Trigger, request.Report, request.Record);
            // Kept in the joiners' list and not a decision: Mute On Join is the group's setting, not a verdict.
            job.Note.NewJoiner = request.NewJoiner || request.Source == ActionSource.MuteOnJoin;
            return job;
        }

        private static DateTime Required(ActionRequest request) =>
            request.UntilUtc ?? throw new ArgumentException($"A {request.Kind} needs an end", nameof(request));
    }
}
