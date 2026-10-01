using System.Collections.Generic;
using System.Threading.Tasks;
using Enforcer5.Models;

namespace Enforcer5.Data
{
    /// <summary>
    /// Per-chat handling of posts made as a channel: whether it is on, which channels are allowed,
    /// and a short-lived cache of the chat's linked channel.
    /// </summary>
    internal interface IChannelPostRepository
    {
        /// <summary>
        /// Everything the message filter needs about <paramref name="senderChatId"/> posting in
        /// <paramref name="chatId"/>, in one round trip.
        /// </summary>
        Task<ChannelPostPolicy> GetPolicyAsync(long chatId, long senderChatId);

        Task<bool> IsEnabledAsync(long chatId);

        Task SetEnabledAsync(long chatId, bool enabled);

        /// <summary>The chat's allowlist, in no particular order.</summary>
        Task<IReadOnlyList<AllowedChannel>> GetAllowedAsync(long chatId);

        Task<long> CountAllowedAsync(long chatId);

        /// <summary>Adds the channel. False if it was already allowed.</summary>
        Task<bool> AllowAsync(long chatId, AllowedChannel channel);

        /// <summary>Removes the channel. False if it was not allowed.</summary>
        Task<bool> DisallowAsync(long chatId, long channelId);

        /// <summary>Caches the chat's linked channel for a short while; 0 means it has none.</summary>
        Task SetLinkedChannelAsync(long chatId, long linkedChannelId);
    }
}
