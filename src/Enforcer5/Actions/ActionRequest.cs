using System;
using Enforcer5.Enforcement;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Telegram.Bot.Types;

namespace Enforcer5.Actions
{
    /// <summary>
    /// An action on a member that goes through the action seam (<c>docs/action-seam.md</c>). Routine deletes
    /// (flood, locks, housekeeping) and raid mode's lock of the group stay direct queue jobs.
    /// </summary>
    internal enum ActionKind
    {
        Ban,
        TempBan,
        Unban,
        Kick,
        Mute,
        TempMute,
        Unmute,
        /// <summary>One warn (plus any prewarns, which it converts). Reaching the limit asks for an escalation.</summary>
        Warn,
        /// <summary>One prewarn: counted, converted into warns at the next warn.</summary>
        Prewarn,
        /// <summary>One strike for blocked media. The caller acts at the media limit.</summary>
        MediaStrike,
        /// <summary>Takes one warn of <see cref="ActionRequest.WarnKind"/> off.</summary>
        RemoveWarn,
        /// <summary>Clears the warns of <see cref="ActionRequest.WarnKind"/> (and media warns, if asked).</summary>
        ResetWarns,
        /// <summary>
        /// A message deleted because an admin asked (/delmsg, /d, a report's Delete button): a decision about
        /// its author. <see cref="ActionRequest.TargetId"/> is the author, 0 when not known.
        /// </summary>
        Delete,
        /// <summary>A channel banned from posting in the group; <see cref="ActionRequest.TargetId"/> is the channel.</summary>
        BanChannel,
        /// <summary>A channel allowed to post in the group again.</summary>
        UnbanChannel
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
        Expiry,
        /// <summary>A post made as a channel the group does not allow.</summary>
        ChannelPost,
        /// <summary>An admin's /batch: one of many members on a list, confirmed by an admin.</summary>
        Batch
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
        /// <summary>
        /// The counter of the member's ban record (<c>ban:{user}</c>, shown by /user) this adds one to: ban,
        /// tempban, kick, flood, maxWarn, ... Null counts nothing.
        /// </summary>
        public string Stat { get; init; }
        /// <summary>A ban: why, as /status shows it. Null writes nothing.</summary>
        public string BanListReason { get; init; }
        /// <summary>A ban: the name kept with <see cref="BanListReason"/>.</summary>
        public string BanListName { get; init; }
        /// <summary>RemoveWarn and ResetWarns: which counter.</summary>
        public WarnKind WarnKind { get; init; }
        /// <summary>ResetWarns of warns: the media warns go too (the reset button under a warn).</summary>
        public bool AlsoMediaWarns { get; init; }
        /// <summary>
        /// A mute of someone who just joined (Mute On Join, raid quarantine): kept in the joiners' list, and
        /// no decision, since it is the group's setting and not a verdict on the member.
        /// </summary>
        public bool NewJoiner { get; init; }
        /// <summary>Delete: the message.</summary>
        public int MessageId { get; init; }
        /// <summary>Delete: the message itself, when the caller has it (what the decision is about).</summary>
        public Message About { get; init; }
        /// <summary>
        /// The action's own line for the group's log channel, posted once it went through (<see cref="ActionLog"/>).
        /// Null: an admin's command is logged as typed, anything else not at all.
        /// </summary>
        public ActionLogEntry Log { get; init; }
        /// <summary>The /batch this is an item of.</summary>
        public long? BatchId { get; init; }
    }

    /// <summary>An action's line in the log channel, built from how it went.</summary>
    internal sealed record ActionLogEntry(Func<ActionResult, string> Text)
    {
        /// <summary>
        /// Posted as the bot's own action ("Enforcer ... replying to the member"), digested during a raid, as
        /// <see cref="Service.LogBotAction"/> does. False: posted as written, with <see cref="LinkMessageId"/>.
        /// </summary>
        public bool ByBot { get; init; } = true;
        /// <summary>The group message the post links to, when not <see cref="ByBot"/>.</summary>
        public long? LinkMessageId { get; init; }

        /// <summary>The bot's own line: what it told the group.</summary>
        internal static ActionLogEntry Bot(string text) => text == null ? null : new(_ => text);
    }

    internal enum ActionOutcome
    {
        /// <summary>Telegram carried it out.</summary>
        Done,
        /// <summary>Not yet, but it stays queued and will be; callers treat it as done.</summary>
        Queued,
        /// <summary>Telegram refused it for good, or the target cannot be warned; see <see cref="ActionResult.Refusal"/>.</summary>
        Refused,
        /// <summary>Nothing to take off: no warns of that kind.</summary>
        NothingToDo,
        /// <summary>An action hook said no; see <see cref="ActionResult.BlockedBy"/> and <see cref="ActionResult.BlockMessage"/>.</summary>
        Blocked
    }

    /// <summary>How an action ended, for the caller to tell the group. <see cref="Applied"/> is what was done.</summary>
    internal sealed record ActionResult(ActionRequest Applied, EnforcementResult Enforcement)
    {
        /// <summary>A warn removal with no warns to take off.</summary>
        public bool Nothing { get; init; }
        /// <summary>Warn kinds: the count after it (warns, prewarns or media strikes; for a reset, 0).</summary>
        public long Count { get; init; }
        /// <summary>A warn: the group's limit.</summary>
        public int Max { get; init; }
        /// <summary>A warn: the prewarns it converted into warns.</summary>
        public long Converted { get; init; }
        /// <summary>A warn or strike as written to the warn history, for an action it leads to.</summary>
        public WarningRef Warning { get; init; }
        /// <summary>A warn that reached the limit: the ban or kick that followed, done or not. Null below the limit.</summary>
        public ActionResult Escalation { get; init; }
        /// <summary>
        /// A prewarn where prewarns are half a warn: it was the second, and the two became a warn. Count, Max, Warning and
        /// Escalation are then the warn's.
        /// </summary>
        public bool BecameWarn { get; init; }

        internal ActionOutcome Outcome => Nothing ? ActionOutcome.NothingToDo : Enforcement.Outcome switch
        {
            EnforcementOutcome.Done => ActionOutcome.Done,
            EnforcementOutcome.Queued => ActionOutcome.Queued,
            _ when Enforcement.Refusal == Refusal.Blocked => ActionOutcome.Blocked,
            _ => ActionOutcome.Refused
        };

        /// <summary>Blocked: the hook that said no.</summary>
        internal string BlockedBy => (Enforcement.Error as ActionBlockedException)?.Hook;

        /// <summary>Blocked: what the hook said, for the admin (may be empty).</summary>
        internal string BlockMessage => (Enforcement.Error as ActionBlockedException)?.Message;

        internal Refusal Refusal => Enforcement.Refusal;

        /// <summary>Done, or certain to be done: what callers announce as done.</summary>
        internal bool Succeeded => !Nothing && Enforcement.Succeeded;
    }
}
