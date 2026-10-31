using System;
using Enforcer5.Enforcement;
using Enforcer5.Helpers;

namespace Enforcer5.Actions
{
    /// <summary>
    /// An action on a member that goes through the action seam (<c>docs/action-seam.md</c>). Warns, deletes
    /// and channel bans join in later steps.
    /// </summary>
    internal enum ActionKind
    {
        Ban,
        TempBan,
        Unban,
        Kick,
        Mute,
        TempMute,
        Unmute
    }

    /// <summary>
    /// Who or what asked for an action. Decides later which hooks may run on it (<c>docs/action-seam.md</c>,
    /// section 5); <see cref="ActionSources.Label"/> maps it onto the <c>member_labels</c> source.
    /// </summary>
    internal enum ActionSource
    {
        /// <summary>The bot on its own, from a path not yet given a source of its own.</summary>
        Auto,
        /// <summary>An admin's command.</summary>
        Command,
        /// <summary>An admin's button, other than on a report or a raid alert.</summary>
        Button,
        /// <summary>An admin acting on a report (/report, @admin, a Detection report).</summary>
        Report,
        /// <summary>Raid mode, or an admin's button on a raid alert or link report.</summary>
        Raid,
        /// <summary>A warn that reached the group's limit.</summary>
        Escalation,
        /// <summary>The Detection engine.</summary>
        Detection,
        /// <summary>A filter rule (<c>/filter</c> and its presets).</summary>
        Filter,
        /// <summary>Flood control.</summary>
        Flood,
        /// <summary>Message length, name length and media limits.</summary>
        Limits,
        /// <summary>A global ban enforced in the group.</summary>
        GlobalBan,
        /// <summary>Mute On Join.</summary>
        MuteOnJoin,
        /// <summary>A tempban that ran out.</summary>
        Expiry
    }

    /// <summary>One action on one member in one chat.</summary>
    internal sealed record ActionRequest
    {
        public required ActionKind Kind { get; init; }
        public required long ChatId { get; init; }
        public required long TargetId { get; init; }
        /// <summary>The admin who asked; null when the bot acts on its own.</summary>
        public long? ActorId { get; init; }
        public required ActionSource Source { get; init; }
        public string Reason { get; init; }
        /// <summary>When a tempban or tempmute ends (UTC).</summary>
        public DateTime? UntilUtc { get; init; }
        /// <summary>A ban that also deletes everything the member posted in the chat.</summary>
        public bool RevokeMessages { get; init; }
        /// <summary>The warn that reached the limit, for an escalation.</summary>
        public WarningRef Trigger { get; init; }
        /// <summary>The report it was taken from.</summary>
        public ReportRef Report { get; init; }
        /// <summary>False leaves it out of the sanction history (/kickme).</summary>
        public bool Record { get; init; } = true;
    }

    internal enum ActionOutcome
    {
        /// <summary>Telegram carried it out.</summary>
        Done,
        /// <summary>Not yet, but it stays queued and will be; callers treat it as done.</summary>
        Queued,
        /// <summary>Telegram refused it for good; see <see cref="ActionResult.Refusal"/>.</summary>
        Refused
    }

    /// <summary>How an action ended, for the caller to tell the group. <see cref="Applied"/> is what was done.</summary>
    internal sealed record ActionResult(ActionRequest Applied, EnforcementResult Enforcement)
    {
        internal ActionOutcome Outcome => Enforcement.Outcome switch
        {
            EnforcementOutcome.Done => ActionOutcome.Done,
            EnforcementOutcome.Queued => ActionOutcome.Queued,
            _ => ActionOutcome.Refused
        };

        internal Refusal Refusal => Enforcement.Refusal;

        /// <summary>Done, or certain to be done: what callers announce as done.</summary>
        internal bool Succeeded => Enforcement.Succeeded;
    }
}
