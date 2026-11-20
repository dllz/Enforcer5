using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using StackExchange.Redis;

namespace Enforcer5.Data
{
    /// <summary>One mute from the old Redis keys: <c>mute</c>, <c>tempmute</c> (with its end) or <c>joiner</c>.</summary>
    internal sealed record LegacyMute(long UserId, string Kind, DateTime? UntilUtc);

    /// <summary>
    /// A group's mutes as Redis held them until rollout step 16, read once for the import on the group's first use and
    /// never written. Shared by both editions:
    /// <list type="bullet">
    /// <item><c>chat:{chatId}:muted</c>: set of user ids muted with /mute.</item>
    /// <item><c>chat:{chatId}:mutedJoiners</c>: set of user ids muted by Mute On Join.</item>
    /// <item><c>chat:{chatId}:tempmuted</c>: hash of user id to the tempmute's end, in Unix seconds.</item>
    /// </list>
    /// The old code kept a member in one of them (the latest mute won); should one be in several, a mute for good wins,
    /// then a tempmute, so nobody is unmuted by /unmutenewjoiners who was not muted on joining. Left in place, so a binary
    /// rollback finds them as they were.
    /// </summary>
    internal sealed class LegacyMutes
    {
        private readonly Func<IDatabaseAsync> _database;

        internal LegacyMutes(Func<IDatabaseAsync> database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        internal static string MutedKey(long chatId) => $"chat:{chatId}:muted";
        internal static string JoinersKey(long chatId) => $"chat:{chatId}:mutedJoiners";
        internal static string TempMutedKey(long chatId) => $"chat:{chatId}:tempmuted";

        /// <summary>The group's mutes, one per member; tempmutes that ended and unreadable entries are left out.</summary>
        internal async Task<IReadOnlyList<LegacyMute>> ReadAsync(long chatId, DateTime nowUtc)
        {
            var db = _database();
            var mutedTask = db.SetMembersAsync(MutedKey(chatId), CommandFlags.None);
            var joinersTask = db.SetMembersAsync(JoinersKey(chatId), CommandFlags.None);
            var tempTask = db.HashGetAllAsync(TempMutedKey(chatId), CommandFlags.None);
            await Task.WhenAll(mutedTask, joinersTask, tempTask);

            var mutes = new Dictionary<long, LegacyMute>();
            foreach (var member in joinersTask.Result ?? Array.Empty<RedisValue>())
                if (long.TryParse(member.ToString(), out var id)) mutes[id] = new LegacyMute(id, "joiner", null);
            var now = nowUtc.ToUnixTime();
            foreach (var entry in tempTask.Result ?? Array.Empty<HashEntry>())
                if (long.TryParse(entry.Name.ToString(), out var id) && long.TryParse(entry.Value.ToString(), out var until) &&
                    until > now && until <= DateTimeOffset.MaxValue.ToUnixTimeSeconds())
                    mutes[id] = new LegacyMute(id, "tempmute", DateTimeOffset.FromUnixTimeSeconds(until).UtcDateTime);
            foreach (var member in mutedTask.Result ?? Array.Empty<RedisValue>())
                if (long.TryParse(member.ToString(), out var id)) mutes[id] = new LegacyMute(id, "mute", null);
            return new List<LegacyMute>(mutes.Values);
        }
    }
}
