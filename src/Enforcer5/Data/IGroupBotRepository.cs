using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Enforcer5.Data
{
    /// <summary>What a group decided about a bot; see <c>group_bots</c> and Detection/BotGuard.</summary>
    /// <param name="Status"><c>allowed</c> or <c>blocked</c>.</param>
    /// <param name="Source">How: <c>admin_added</c>, <c>command</c>, <c>present</c>, <c>member_added</c> or <c>summoned</c>.</param>
    internal sealed record GroupBotRecord(long ChatId, long BotId, string Status, string Source, long? SetBy, DateTimeOffset SetAt)
    {
        internal const string Allowed = "allowed";
        internal const string Blocked = "blocked";

        internal bool IsAllowed => Status == Allowed;
    }

    /// <summary>A bot added, summoned, trusted or untrusted, and by whom (<c>bot_events</c>).</summary>
    /// <param name="Kind"><c>added</c>, <c>summoned</c>, <c>trusted</c> or <c>untrusted</c>.</param>
    /// <param name="Action"><c>none</c> or <c>banned</c>: what the bot did about the bot.</param>
    internal sealed record BotEvent(long ChatId, long BotId, long? ActorId, bool ActorWasAdmin, string Kind, string Action,
        string Edition, DateTimeOffset At);

    internal interface IGroupBotRepository
    {
        /// <summary>Every group's decisions, for memory.</summary>
        Task<IReadOnlyList<GroupBotRecord>> AllAsync();

        /// <summary>
        /// Stores the decision. With <paramref name="overwrite"/> false an existing one is kept (a bot found
        /// already in the group never replaces what an admin decided).
        /// </summary>
        Task SetAsync(GroupBotRecord record, bool overwrite);

        Task RecordEventAsync(BotEvent botEvent);
    }
}
