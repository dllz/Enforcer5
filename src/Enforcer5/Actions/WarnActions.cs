using System;
using Enforcer5.Data;
using Enforcer5.Enforcement;
using Enforcer5.Helpers;
using Enforcer5.Models;

namespace Enforcer5.Actions
{
    /// <summary>
    /// The warn actions (<c>docs/action-seam.md</c>, step C): warns, prewarns, media strikes and their removal.
    /// No Telegram call of their own; a warn that reaches the group's limit asks for a ban or kick, which goes
    /// through the seam as its own action (<see cref="ActionSource.Escalation"/>).
    /// </summary>
    internal static class WarnActions
    {
        internal static bool Handles(ActionKind kind) =>
            kind is ActionKind.Warn or ActionKind.Prewarn or ActionKind.MediaStrike or ActionKind.RemoveWarn or ActionKind.ResetWarns;

        internal static ActionResult Execute(ActionRequest request) => request.Kind switch
        {
            ActionKind.Warn => Warn(request),
            ActionKind.Prewarn => Prewarn(request),
            ActionKind.MediaStrike => MediaStrike(request),
            _ => Remove(request)
        };

        private static ActionResult Done(ActionRequest request) => new(request, EnforcementResult.Done);

        /// <summary>Admins and the bot are never warned, as before: refused, and callers say nothing.</summary>
        private static bool Unwarnable(ActionRequest request)
        {
            if (request.TargetId == Bot.Me?.Id) return true;
            try
            {
                return Methods.IsGroupAdmin(request.TargetId, request.ChatId);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Checking whether {request.TargetId} is an admin in {request.ChatId} before a warn");
                return false;
            }
        }

        private static ActionResult Refused(ActionRequest request) =>
            new(request, EnforcementResult.Refuse(Refusal.TargetIsAdmin));

        private static ActionResult Prewarn(ActionRequest request)
        {
            if (Unwarnable(request)) return Refused(request);
            var count = Repositories.Warns.AddAsync(request.ChatId, WarnKind.Prewarn, request.TargetId, 1).GetAwaiter().GetResult();
            var warning = WarnHistory.Added(request.ChatId, request.TargetId, WarnKind.Prewarn, 1, count, null, request.ActorId, request.Reason);
            return Done(request) with { Count = count, Warning = warning };
        }

        private static ActionResult Warn(ActionRequest request)
        {
            if (Unwarnable(request)) return Refused(request);
            var (chatId, userId) = (request.ChatId, request.TargetId);
            var warns = Repositories.Warns;

            // Prewarns turn into warns at the next warn.
            var converted = warns.GetCountAsync(chatId, WarnKind.Prewarn, userId).GetAwaiter().GetResult();
            if (converted > 0)
            {
                warns.ResetAsync(chatId, WarnKind.Prewarn, userId).GetAwaiter().GetResult();
                WarnHistory.Removed(chatId, userId, WarnKind.Prewarn, converted, 0, request.ActorId, "Converted into warns");
            }
            var count = warns.AddAsync(chatId, WarnKind.Warn, userId, 1 + converted).GetAwaiter().GetResult();
            var lifetime = warns.AddLifetimeAsync(chatId, userId, 1 + converted).GetAwaiter().GetResult();
            var warning = WarnHistory.Added(chatId, userId, WarnKind.Warn, 1 + converted, count, lifetime, request.ActorId,
                request.Reason, request.Report);
            ReportHistory.Acted(request.Report, ReportAction.Warned, request.ActorId);
            if (count < 0)
            {
                // A negative count left by old code would have cancelled warns out.
                warns.ResetAsync(chatId, WarnKind.Warn, userId).GetAwaiter().GetResult();
                count = 0;
            }

            var max = Methods.MaxWarns(chatId);
            var result = Done(request) with { Count = count, Max = max, Converted = converted, Warning = warning };
            if (count < max) return result;

            // The limit: a ban or kick of its own, which may be refused. Only once it went through are the warns
            // reset; otherwise they stay and the caller tells the admins (Daniel, 2026-10-31).
            var kick = Methods.WarnAction(chatId) == "kick";
            var escalation = ActionSeam.Run(new ActionRequest
            {
                Kind = kick ? ActionKind.Kick : ActionKind.Ban,
                ChatId = chatId,
                TargetId = userId,
                Source = ActionSource.Escalation,
                Reason = "Max warns",
                Trigger = warning,
                Report = request.Report,
                Stat = kick ? null : "maxWarn"
            });
            if (escalation.Succeeded)
            {
                warns.ResetAsync(chatId, WarnKind.Warn, userId).GetAwaiter().GetResult();
                WarnHistory.Reset(chatId, userId, WarnKind.Warn, count, null, "Max warns");
            }
            return result with { Escalation = escalation };
        }

        private static ActionResult MediaStrike(ActionRequest request)
        {
            if (Unwarnable(request)) return Refused(request);
            var count = Repositories.Warns.AddAsync(request.ChatId, WarnKind.Media, request.TargetId, 1).GetAwaiter().GetResult();
            var warning = WarnHistory.Added(request.ChatId, request.TargetId, WarnKind.Media, 1, count, null, request.ActorId, request.Reason);
            return Done(request) with { Count = count, Warning = warning };
        }

        private static ActionResult Remove(ActionRequest request)
        {
            var change = new WarnChange(request.WarnKind, request.Kind == ActionKind.ResetWarns, request.TargetId, request.AlsoMediaWarns);
            if (WarnRemovals.CountAsync(Repositories.Warns, request.ChatId, change).GetAwaiter().GetResult() == 0)
                return Done(request) with { Nothing = true };
            var left = WarnRemovals.ApplyAsync(Repositories.Warns, Repositories.WarnReasons, request.ChatId, change, request.Reason,
                request.ActorId).GetAwaiter().GetResult();
            return Done(request) with { Count = left };
        }
    }
}
