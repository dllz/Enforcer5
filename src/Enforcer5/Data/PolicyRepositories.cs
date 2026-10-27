using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Detection;
using Enforcer5.Models;
using StackExchange.Redis;

namespace Enforcer5.Data
{
    /// <summary>
    /// The inline bot blocklist, kept in the group's <see cref="DetectionPolicy"/> (PostgreSQL
    /// <c>group_filters</c>, target <c>inline_bot</c>) since Detection step 2. Changes throw when
    /// PostgreSQL is not ready.
    /// </summary>
    internal sealed class PolicyInlineBotBlockRepository : IInlineBotBlockRepository
    {
        public async Task<IReadOnlyList<InlineBotBlock>> GetAsync(long chatId) =>
            (await DetectionSettings.GetAsync(chatId)).InlineBots;

        public async Task<long> CountAsync(long chatId) => (await GetAsync(chatId)).Count;

        public async Task<bool> AddAsync(long chatId, InlineBotBlock block)
        {
            if (block == null) throw new ArgumentNullException(nameof(block));
            var added = false;
            await DetectionSettings.UpdateAsync(chatId, policy =>
            {
                added = !policy.InlineBots.Contains(block);
                return added ? policy with { InlineBots = policy.InlineBots.Append(block).ToList() } : policy;
            });
            return added;
        }

        public async Task<bool> RemoveAsync(long chatId, InlineBotBlock block)
        {
            var removed = false;
            await DetectionSettings.UpdateAsync(chatId, policy =>
            {
                removed = policy.InlineBots.Contains(block);
                return removed ? policy with { InlineBots = policy.InlineBots.Where(b => b != block).ToList() } : policy;
            });
            return removed;
        }
    }

    /// <summary>
    /// The sticker pack blocklist, kept in the group's <see cref="DetectionPolicy"/> (PostgreSQL
    /// <c>group_filters</c>, target <c>sticker_set</c>) since Detection step 2.
    /// </summary>
    internal sealed class PolicyStickerSetBlockRepository : IStickerSetBlockRepository
    {
        public async Task<IReadOnlyList<string>> GetAsync(long chatId) =>
            (await DetectionSettings.GetAsync(chatId)).StickerSets;

        public async Task<long> CountAsync(long chatId) => (await GetAsync(chatId)).Count;

        public async Task<bool> ContainsAsync(long chatId, string setName) =>
            (await GetAsync(chatId)).Contains(Required(setName), StringComparer.Ordinal);

        public async Task<bool> AddAsync(long chatId, string setName)
        {
            Required(setName);
            var added = false;
            await DetectionSettings.UpdateAsync(chatId, policy =>
            {
                added = !policy.StickerSets.Contains(setName, StringComparer.Ordinal);
                return added ? policy with { StickerSets = policy.StickerSets.Append(setName).ToList() } : policy;
            });
            return added;
        }

        public async Task<bool> RemoveAsync(long chatId, string setName)
        {
            Required(setName);
            var removed = false;
            await DetectionSettings.UpdateAsync(chatId, policy =>
            {
                removed = policy.StickerSets.Contains(setName, StringComparer.Ordinal);
                return removed
                    ? policy with { StickerSets = policy.StickerSets.Where(s => !string.Equals(s, setName, StringComparison.Ordinal)).ToList() }
                    : policy;
            });
            return removed;
        }

        private static string Required(string setName) =>
            string.IsNullOrEmpty(setName) ? throw new ArgumentException("A pack name is required.", nameof(setName)) : setName;
    }

    /// <summary>
    /// Posts made as a channel: whether the group removes them and which channels may post anyway,
    /// kept in the group's <see cref="DetectionPolicy"/> (PostgreSQL <c>detection_policies</c>,
    /// check <c>channel_posts</c>) since Detection step 2. The group's linked channel is only
    /// cached, in Redis, for <see cref="RedisChannelPostRepository.LinkedChannelTtl"/>.
    /// </summary>
    internal sealed class PolicyChannelPostRepository : IChannelPostRepository
    {
        private readonly Func<IDatabaseAsync> _redis;

        public PolicyChannelPostRepository(Func<IDatabaseAsync> redis)
        {
            _redis = redis ?? throw new ArgumentNullException(nameof(redis));
        }

        public async Task<ChannelPostPolicy> GetPolicyAsync(long chatId, long senderChatId)
        {
            var policy = DetectionSettings.GetAsync(chatId);
            var linked = _redis().StringGetAsync(LinkedKey(chatId));
            await Task.WhenAll(policy, linked);
            long? linkedId = linked.Result.HasValue && long.TryParse(linked.Result.ToString(), out var id) ? id : null;
            var settings = policy.Result.ChannelPosts;
            return new ChannelPostPolicy(settings.Enabled, settings.IsAllowed(senderChatId), linkedId);
        }

        public async Task<bool> IsEnabledAsync(long chatId) => (await DetectionSettings.GetAsync(chatId)).ChannelPosts.Enabled;

        public Task SetEnabledAsync(long chatId, bool enabled) =>
            DetectionSettings.UpdateAsync(chatId, policy => policy with { ChannelPosts = policy.ChannelPosts with { Enabled = enabled } });

        public async Task<IReadOnlyList<AllowedChannel>> GetAllowedAsync(long chatId) =>
            (await DetectionSettings.GetAsync(chatId)).ChannelPosts.Allowed;

        public async Task<long> CountAllowedAsync(long chatId) => (await GetAllowedAsync(chatId)).Count;

        public async Task<bool> AllowAsync(long chatId, AllowedChannel channel)
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));
            var added = false;
            await DetectionSettings.UpdateAsync(chatId, policy =>
            {
                added = !policy.ChannelPosts.IsAllowed(channel.Id);
                return added
                    ? policy with
                    {
                        ChannelPosts = policy.ChannelPosts with
                        {
                            Allowed = policy.ChannelPosts.Allowed.Append(channel with { Title = channel.Title ?? "" }).ToList()
                        }
                    }
                    : policy;
            });
            return added;
        }

        public async Task<bool> DisallowAsync(long chatId, long channelId)
        {
            var removed = false;
            await DetectionSettings.UpdateAsync(chatId, policy =>
            {
                removed = policy.ChannelPosts.IsAllowed(channelId);
                return removed
                    ? policy with
                    {
                        ChannelPosts = policy.ChannelPosts with
                        {
                            Allowed = policy.ChannelPosts.Allowed.Where(c => c.Id != channelId).ToList()
                        }
                    }
                    : policy;
            });
            return removed;
        }

        public Task SetLinkedChannelAsync(long chatId, long linkedChannelId) =>
            _redis().StringSetAsync(LinkedKey(chatId), linkedChannelId, RedisChannelPostRepository.LinkedChannelTtl, When.Always, CommandFlags.None);

        private static RedisKey LinkedKey(long chatId) => $"chat:{chatId}:linkedchannel";
    }
}
