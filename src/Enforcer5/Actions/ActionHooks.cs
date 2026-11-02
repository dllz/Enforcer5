using System;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Enforcement;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Types;

namespace Enforcer5.Actions
{
    /// <summary>
    /// Code that runs before an action (and may block or change it) or after it (and may react):
    /// <c>docs/action-seam.md</c>, sections 5 and 6. In-process C# hooks for now; the script host of
    /// <c>docs/scripting-design.md</c> becomes one of them.
    /// </summary>
    internal interface IActionHook
    {
        /// <summary>Named in a blocked action's answer and in what is recorded about the hook.</summary>
        string Name { get; }

        /// <summary>
        /// Whether the hook wants <paramref name="request"/> at all. Called on the action's path, before any
        /// budget: in memory only, no I/O.
        /// </summary>
        bool Wants(ActionRequest request);

        /// <summary>Allow, block or change the action. Within <see cref="ActionHooks.BeforeBudget"/>, or it is allowed.</summary>
        Task<HookVerdict> BeforeAsync(ActionRequest request) => Task.FromResult(HookVerdict.Allow);

        /// <summary>The action happened (or was refused or blocked). Runs in the background; nobody waits for it.</summary>
        Task AfterAsync(ActionResult result) => Task.CompletedTask;
    }

    /// <summary>What a before hook says about an action.</summary>
    internal sealed record HookVerdict
    {
        internal static readonly HookVerdict Allow = new();

        /// <summary>Not null: the action does not happen, and this is what the admin is told (may be empty).</summary>
        public string BlockMessage { get; init; }
        /// <summary>A new reason for the action.</summary>
        public string Reason { get; init; }
        /// <summary>A new end for a tempban or tempmute (UTC). Ignored for any other kind.</summary>
        public DateTime? UntilUtc { get; init; }

        internal bool Blocks => BlockMessage != null;

        internal static HookVerdict Block(string message) => new() { BlockMessage = message ?? "" };
    }

    /// <summary>The refusal a blocked action carries: which hook said no, and what it said.</summary>
    internal sealed class ActionBlockedException : Exception
    {
        internal ActionBlockedException(string hook, string message) : base(message) => Hook = hook;

        internal string Hook { get; }
    }

    /// <summary>
    /// The registered hooks, and when they run (section 5): before hooks only on what an admin chose (a
    /// command, a button, a report) and on a warn's escalation, never on the bot's own checks and defences, and
    /// never on the group creator's or a dev's actions, so no hook can lock the owner out; after hooks on
    /// everything but the defences (flood, raid, global bans, Mute On Join), which act on hundreds of members
    /// at once. A hook that throws or overruns its budget counts as allow, and is reported.
    /// </summary>
    internal static class ActionHooks
    {
        /// <summary>How long one before hook may take on the action's path.</summary>
        internal static TimeSpan BeforeBudget = TimeSpan.FromMilliseconds(200);

        /// <summary>How long one after hook may take before it is reported (it is not stopped).</summary>
        internal static TimeSpan AfterBudget = TimeSpan.FromSeconds(5);

        // Replaced whole on change, so the action path reads it without a lock. Empty in production for now.
        private static volatile IActionHook[] _hooks = Array.Empty<IActionHook>();

        internal static void Add(IActionHook hook)
        {
            lock (typeof(ActionHooks)) _hooks = _hooks.Append(hook).ToArray();
        }

        internal static void Remove(IActionHook hook)
        {
            lock (typeof(ActionHooks)) _hooks = _hooks.Where(h => h != hook).ToArray();
        }

        internal static void ResetForTests() => _hooks = Array.Empty<IActionHook>();

        /// <summary>Whether before hooks may block or change an action from <paramref name="source"/>.</summary>
        internal static bool BeforeRuns(ActionSource source) =>
            source is ActionSource.Command or ActionSource.Button or ActionSource.Report or ActionSource.Escalation
                or ActionSource.Batch;

        /// <summary>Whether after hooks hear about an action from <paramref name="source"/>.</summary>
        internal static bool AfterRuns(ActionSource source) =>
            source is not (ActionSource.Flood or ActionSource.Raid or ActionSource.GlobalBan or ActionSource.MuteOnJoin);

        /// <summary>
        /// Runs the before hooks on <paramref name="request"/>, in the order they were added. Returns the request
        /// as the hooks changed it, or a blocked result (the first block wins). Costs one array read when no
        /// hook is registered.
        /// </summary>
        internal static (ActionRequest Request, ActionResult Blocked) Before(ActionRequest request)
        {
            var hooks = _hooks;
            if (hooks.Length == 0 || !BeforeRuns(request.Source)) return (request, null);
            var wanting = hooks.Where(h => Wants(h, request)).ToArray();
            if (wanting.Length == 0 || Exempt(request)) return (request, null);
            foreach (var hook in wanting)
            {
                var verdict = Ask(hook, request);
                if (verdict == null) continue;
                if (verdict.Blocks)
                    return (request, new ActionResult(request,
                        EnforcementResult.Refuse(Refusal.Blocked, new ActionBlockedException(hook.Name, verdict.BlockMessage))));
                request = request with
                {
                    Reason = verdict.Reason ?? request.Reason,
                    UntilUtc = request.Kind is ActionKind.TempBan or ActionKind.TempMute && verdict.UntilUtc is { } until
                        ? until
                        : request.UntilUtc
                };
            }
            return (request, null);
        }

        /// <summary>Hands <paramref name="result"/> to the after hooks, in the background. Never waits, never throws.</summary>
        internal static void After(ActionResult result)
        {
            var hooks = _hooks;
            if (hooks.Length == 0 || !AfterRuns(result.Applied.Source)) return;
            foreach (var hook in hooks.Where(h => Wants(h, result.Applied)))
            {
                Bot.Dispatch(async () =>
                {
                    try
                    {
                        var after = hook.AfterAsync(result) ?? Task.CompletedTask;
                        if (!Clock.Wait(after, AfterBudget))
                            Over(hook, "after", result.Applied);
                        else await after;
                    }
                    catch (Exception e)
                    {
                        ErrorReporter.Report(e, $"The after hook {hook.Name} on the {result.Applied.Kind} of {result.Applied.TargetId} in {result.Applied.ChatId}");
                    }
                }, "ActionHook.After");
            }
        }

        private static bool Wants(IActionHook hook, ActionRequest request)
        {
            try
            {
                return hook.Wants(request);
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"Asking the hook {hook.Name} whether it wants the {request.Kind} of {request.TargetId} in {request.ChatId}");
                return false;
            }
        }

        // Null: allow, as for a hook that threw or overran.
        private static HookVerdict Ask(IActionHook hook, ActionRequest request)
        {
            try
            {
                // Called inline: a hook that answers at once costs no thread hop, which under load could
                // itself overrun the budget. What it leaves running (I/O, a script) gets the budget. A
                // hook's own CPU is its business: the script host bounds a script's with its engine's limits.
                var asked = hook.BeforeAsync(request);
                if (asked == null) return null;
                if (!Clock.Wait(asked, BeforeBudget))
                {
                    Over(hook, "before", request);
                    return null;
                }
                return asked.GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                ErrorReporter.Report(e, $"The before hook {hook.Name} on the {request.Kind} of {request.TargetId} in {request.ChatId} (allowed)");
                return null;
            }
        }

        private static void Over(IActionHook hook, string when, ActionRequest request) =>
            ErrorReporter.Report(new TimeoutException($"The {when} hook {hook.Name} took longer than its budget"),
                $"The {when} hook {hook.Name} on the {request.Kind} of {request.TargetId} in {request.ChatId} (allowed)");

        /// <summary>The group creator's and the devs' actions are never held up by a hook.</summary>
        private static bool Exempt(ActionRequest request)
        {
            if (request.ActorId is not { } actor) return false;
            if (Constants.Devs.Contains(actor)) return true;
            try
            {
                return AdminDirectory.Get(request.ChatId).Any(m => m is ChatMemberOwner && m.User.Id == actor);
            }
            catch (Exception e)
            {
                // Not knowing who the creator is: the hooks run, as for anyone.
                ErrorReporter.Report(e, $"Reading the admins of {request.ChatId} for the action hooks");
                return false;
            }
        }
    }
}
