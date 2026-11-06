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

        private static WarningRef Ref(WarnChanged change) => new() { EventId = change.EventId };

        private static ActionResult Prewarn(ActionRequest request)
        {
            if (Unwarnable(request)) return Refused(request);
            var (chatId, userId) = (request.ChatId, request.TargetId);
            var added = WarnHistory.Apply(WarnHistory.Op(chatId, userId, WarnKind.Prewarn, WarnOperation.Add, 1,
                request.ActorId, request.Reason));
            var result = Done(request) with { Count = added.After, Warning = Ref(added) };
            // Half a warn (Daniel, 2026-11-06): the second prewarn makes the two a warn at once, which may reach the limit.
            if (!Methods.PrewarnIsHalf(chatId) || added.After < 2) return result;
            WarnHistory.Apply(WarnHistory.Op(chatId, userId, WarnKind.Prewarn, WarnOperation.Remove, 2,
                request.ActorId, "Two pre-warns make a warning"));
            return AddWarn(request, convertPrewarns: false) with { BecameWarn = true };
        }

        private static ActionResult Warn(ActionRequest request)
        {
            if (Unwarnable(request)) return Refused(request);
            // Where prewarns are half a warn they stay what they are; otherwise they turn into warns at the next warn.
            return AddWarn(request, convertPrewarns: !Methods.PrewarnIsHalf(request.ChatId));
        }

        private static ActionResult AddWarn(ActionRequest request, bool convertPrewarns)
        {
            var (chatId, userId) = (request.ChatId, request.TargetId);
            var converted = convertPrewarns
                ? Repositories.Warns.GetCountAsync(chatId, WarnKind.Prewarn, userId).GetAwaiter().GetResult()
                : 0;
            if (converted > 0)
                WarnHistory.Apply(WarnHistory.Op(chatId, userId, WarnKind.Prewarn, WarnOperation.Remove, converted,
                    request.ActorId, "Converted into warns"));
            var added = WarnHistory.Apply(WarnHistory.Op(chatId, userId, WarnKind.Warn, WarnOperation.Add, 1 + converted,
                request.ActorId, request.Reason, request.Report), request.Report);
            var count = added.After;
            var warning = Ref(added);
            ReportHistory.Acted(request.Report, ReportAction.Warned, request.ActorId);

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
                WarnHistory.Apply(WarnHistory.Op(chatId, userId, WarnKind.Warn, WarnOperation.Reset, 0, null, "Max warns"));
            return result with { Escalation = escalation };
        }

        private static ActionResult MediaStrike(ActionRequest request)
        {
            if (Unwarnable(request)) return Refused(request);
            var added = WarnHistory.Apply(WarnHistory.Op(request.ChatId, request.TargetId, WarnKind.Media, WarnOperation.Add, 1,
                request.ActorId, request.Reason));
            return Done(request) with { Count = added.After, Warning = Ref(added) };
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
