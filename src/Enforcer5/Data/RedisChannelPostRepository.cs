using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Enforcer5.Models;
using StackExchange.Redis;

namespace Enforcer5.Data
{
    /// <summary>
    /// Redis layout:
    /// <list type="bullet">
    /// <item><c>chat:{chatId}:channelposts</c>: hash, field <c>enabled</c> = yes/no.</item>
    /// <item><c>chat:{chatId}:allowedchannels</c>: hash of channel id to title.</item>
    /// <item><c>chat:{chatId}:linkedchannel</c>: the linked channel id (0 = none), expiring after
    /// <see cref="LinkedChannelTtl"/> so a newly linked or unlinked channel is picked up soon.</item>
    /// </list>
    /// </summary>
    internal sealed class RedisChannelPostRepository : IChannelPostRepository
    {
        internal static readonly TimeSpan LinkedChannelTtl = TimeSpan.FromMinutes(10);

        // Resolved per call: the connection only exists once Redis.Start() has run.
        private readonly Func<IDatabaseAsync> _database;

        public RedisChannelPostRepository(Func<IDatabaseAsync> database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public async Task<ChannelPostPolicy> GetPolicyAsync(long chatId, long senderChatId)
        {
            var db = _database();
            var enabled = db.HashGetAsync(SettingsKey(chatId), "enabled");
            var allowed = db.HashExistsAsync(AllowedKey(chatId), senderChatId);
            var linked = db.StringGetAsync(LinkedKey(chatId));
            await Task.WhenAll(enabled, allowed, linked);

            long? linkedId = linked.Result.HasValue && long.TryParse(linked.Result.ToString(), out var id) ? id : null;
            return new ChannelPostPolicy(enabled.Result == "yes", allowed.Result, linkedId);
        }

        public async Task<bool> IsEnabledAsync(long chatId) =>
            await _database().HashGetAsync(SettingsKey(chatId), "enabled") == "yes";

        public Task SetEnabledAsync(long chatId, bool enabled) =>
            _database().HashSetAsync(SettingsKey(chatId), "enabled", enabled ? "yes" : "no");

        public async Task<IReadOnlyList<AllowedChannel>> GetAllowedAsync(long chatId)
        {
            var entries = await _database().HashGetAllAsync(AllowedKey(chatId));
            var channels = new List<AllowedChannel>(entries.Length);
            foreach (var entry in entries)
            {
                if (long.TryParse(entry.Name.ToString(), out var id))
                    channels.Add(new AllowedChannel(id, entry.Value.ToString()));
                else
                    LogHelper.Error($"Ignoring unreadable allowed channel '{entry.Name}' in {chatId}");
            }
            return channels;
        }

        public Task<long> CountAllowedAsync(long chatId) => _database().HashLengthAsync(AllowedKey(chatId));

        public Task<bool> AllowAsync(long chatId, AllowedChannel channel)
        {
            if (channel == null) throw new ArgumentNullException(nameof(channel));
            return _database().HashSetAsync(AllowedKey(chatId), channel.Id, channel.Title ?? "", When.NotExists, CommandFlags.None);
        }

        public Task<bool> DisallowAsync(long chatId, long channelId) =>
            _database().HashDeleteAsync(AllowedKey(chatId), channelId);

        public Task SetLinkedChannelAsync(long chatId, long linkedChannelId) =>
            _database().StringSetAsync(LinkedKey(chatId), linkedChannelId, LinkedChannelTtl, When.Always, CommandFlags.None);

        private static RedisKey SettingsKey(long chatId) => $"chat:{chatId}:channelposts";
        private static RedisKey AllowedKey(long chatId) => $"chat:{chatId}:allowedchannels";
        private static RedisKey LinkedKey(long chatId) => $"chat:{chatId}:linkedchannel";
    }
}
