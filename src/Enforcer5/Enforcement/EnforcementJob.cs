using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Newtonsoft.Json;
using Telegram.Bot.Types;

namespace Enforcer5.Enforcement
{
    /// <summary>What an enforcement job does.</summary>
    internal enum EnforcementKind
    {
        /// <summary>Sets the chat's own permissions (raid lock and unlock).</summary>
        SetChatPermissions,
        Ban,
        /// <summary>Ban, then lift it at once: out of the chat, free to come back. Two calls.</summary>
        Kick,
        BanSenderChat,
        Mute,
        Delete,
        Unban,
        Unmute,
        UnbanSenderChat
    }

    /// <summary>How a job ended for the caller who submitted it.</summary>
    internal enum EnforcementOutcome
    {
        /// <summary>Telegram carried it out.</summary>
        Done,
        /// <summary>Not yet (rate limited, or the bot lost its rights for a moment), but it stays queued and will be.</summary>
        Queued,
        /// <summary>Telegram refused it for good; see <see cref="EnforcementResult.Refusal"/>.</summary>
        Refused
    }

    /// <summary>Why Telegram refused a job, for the caller to tell the group.</summary>
    internal enum Refusal
    {
        None,
        /// <summary>The bot is not an admin there or lacks the right. The job waits a while for the right to come back.</summary>
        BotNotAdmin,
        /// <summary>The target is an admin or the owner, whom bots can never restrict.</summary>
        TargetIsAdmin,
        /// <summary>Telegram has no such member or user.</summary>
        NotFound,
        Other
    }

    internal readonly record struct EnforcementResult(EnforcementOutcome Outcome, Refusal Refusal = Refusal.None, Exception Error = null)
    {
        /// <summary>Done, or certain to be done: what callers announce as done.</summary>
        internal bool Succeeded => Outcome != EnforcementOutcome.Refused;

        internal static EnforcementResult Done => new(EnforcementOutcome.Done);
        internal static EnforcementResult Queued => new(EnforcementOutcome.Queued);
        internal static EnforcementResult Refuse(Refusal why, Exception error = null) => new(EnforcementOutcome.Refused, why, error);
    }

    /// <summary>
    /// What to record once the job is done: the sanction history and the Redis bookkeeping that
    /// used to follow each call. Stored with the job, so a job carried out after a restart is
    /// recorded the same way.
    /// </summary>
    internal sealed class EnforcementNote
    {
        public string Reason { get; set; }
        public long? ActorId { get; set; }
        public long? TriggerEventId { get; set; }
        public long? ReportId { get; set; }
        /// <summary>False leaves it out of the sanction history (/kickme).</summary>
        public bool Record { get; set; } = true;
        /// <summary>A mute by Mute On Join (or raid quarantine), kept in the joiners' list.</summary>
        public bool NewJoiner { get; set; }
        /// <summary>For an unban: lifted by an admin, or a tempban that ran out.</summary>
        public BanEnd End { get; set; } = BanEnd.Unbanned;
    }

    /// <summary>
    /// One ban, kick, mute, delete or permission change, as kept in the queue and its journal.
    /// <see cref="UntilUtc"/> is absolute: a tempban carried out late still ends when it was meant to.
    /// </summary>
    internal sealed class EnforcementJob
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public EnforcementKind Kind { get; set; }
        public long ChatId { get; set; }
        /// <summary>The user, or the channel for a sender-chat ban. Zero for chat-wide jobs.</summary>
        public long TargetId { get; set; }
        public DateTime? UntilUtc { get; set; }
        /// <summary>A ban that also deletes everything the user posted in the chat.</summary>
        public bool RevokeMessages { get; set; }
        public List<int> MessageIds { get; set; }
        public ChatPermissions Permissions { get; set; }
        public EnforcementNote Note { get; set; } = new EnforcementNote();
        /// <summary>How far a two-call job (a kick) got: 1 once the ban went through.</summary>
        public int Stage { get; set; }
        public int Attempts { get; set; }
        /// <summary>Not to be carried out before this time: a notice deleted once it has said its piece.</summary>
        public DateTime? NotBeforeUtc { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime DueUtc { get; set; }
        /// <summary>When Telegram first said the bot lacks the right, while the job waits for it to come back.</summary>
        public DateTime? RefusedSinceUtc { get; set; }
        /// <summary>Order of submission, for first come, first served within a chat.</summary>
        public long Sequence { get; set; }

        // Kept in memory only: what the submitter is waiting on, and references whose ids may only
        // be known once earlier history writes ran.
        [JsonIgnore] internal TaskCompletionSource<EnforcementResult> Completion;
        [JsonIgnore] internal WarningRef Trigger;
        [JsonIgnore] internal ReportRef Report;
        [JsonIgnore] internal ErrorContext Context;
        /// <summary>Deletes folded into this one (see EnforcementQueue), finished with it.</summary>
        [JsonIgnore] internal List<EnforcementJob> Riders = new();

        /// <summary>Another job with the same key while this one waits is the same job.</summary>
        [JsonIgnore]
        internal string DedupeKey => Kind == EnforcementKind.Delete
            ? null
            : $"{Kind}:{ChatId}:{TargetId}:{UntilUtc?.Ticks}";

        /// <summary>Lower goes first within a chat: closing the door, then removing people, then cleaning up.</summary>
        [JsonIgnore]
        internal int Priority => Kind switch
        {
            EnforcementKind.SetChatPermissions => 0,
            EnforcementKind.Ban or EnforcementKind.Kick or EnforcementKind.BanSenderChat => 1,
            EnforcementKind.Mute => 2,
            EnforcementKind.Delete => 3,
            _ => 4
        };

        internal static EnforcementJob Ban(long chatId, long userId, DateTime? untilUtc = null, bool revokeMessages = false) =>
            new EnforcementJob { Kind = EnforcementKind.Ban, ChatId = chatId, TargetId = userId, UntilUtc = untilUtc, RevokeMessages = revokeMessages };

        internal static EnforcementJob Kick(long chatId, long userId) =>
            new EnforcementJob { Kind = EnforcementKind.Kick, ChatId = chatId, TargetId = userId };

        internal static EnforcementJob Unban(long chatId, long userId, BanEnd end) =>
            new EnforcementJob { Kind = EnforcementKind.Unban, ChatId = chatId, TargetId = userId, Note = { End = end } };

        internal static EnforcementJob Mute(long chatId, long userId, DateTime? untilUtc = null) =>
            new EnforcementJob { Kind = EnforcementKind.Mute, ChatId = chatId, TargetId = userId, UntilUtc = untilUtc };

        internal static EnforcementJob Unmute(long chatId, long userId) =>
            new EnforcementJob { Kind = EnforcementKind.Unmute, ChatId = chatId, TargetId = userId };

        internal static EnforcementJob Delete(long chatId, params int[] messageIds) =>
            new EnforcementJob { Kind = EnforcementKind.Delete, ChatId = chatId, MessageIds = new List<int>(messageIds) };

        internal static EnforcementJob BanSenderChat(long chatId, long senderChatId) =>
            new EnforcementJob { Kind = EnforcementKind.BanSenderChat, ChatId = chatId, TargetId = senderChatId };

        internal static EnforcementJob UnbanSenderChat(long chatId, long senderChatId) =>
            new EnforcementJob { Kind = EnforcementKind.UnbanSenderChat, ChatId = chatId, TargetId = senderChatId };

        internal static EnforcementJob SetPermissions(long chatId, ChatPermissions permissions) =>
            new EnforcementJob { Kind = EnforcementKind.SetChatPermissions, ChatId = chatId, Permissions = permissions };

        internal EnforcementJob For(string reason, long? actorId, WarningRef trigger = null, ReportRef report = null, bool record = true)
        {
            Note.Reason = reason;
            Note.ActorId = actorId;
            Note.Record = record;
            Trigger = trigger;
            Report = report;
            return this;
        }

        internal string ToJson() => JsonConvert.SerializeObject(this);

        internal static EnforcementJob FromJson(string json) => JsonConvert.DeserializeObject<EnforcementJob>(json);

        public override string ToString() => $"{Kind} {TargetId} in {ChatId}";
    }
}
