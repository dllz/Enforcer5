using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Enforcer5.Models;
using StackExchange.Redis;

namespace Enforcer5.Data
{
    /// <summary>
    /// Redis-backed blocklist: one set per chat at <c>chat:{chatId}:blockedinline</c>. Members are
    /// <c>u:{username}</c> or <c>r:{pattern}</c>; the prefix keeps the kind explicit in storage
    /// rather than inferring it from the admin-facing syntax.
    /// </summary>
    internal sealed class RedisInlineBotBlockRepository : IInlineBotBlockRepository
    {
        private const string UsernamePrefix = "u:";
        private const string PatternPrefix = "r:";

        // Resolved per call: the connection is only established by Redis.Start(), after this
        // repository has been constructed.
        private readonly Func<IDatabaseAsync> _database;

        public RedisInlineBotBlockRepository(Func<IDatabaseAsync> database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        public async Task<IReadOnlyList<InlineBotBlock>> GetAsync(long chatId)
        {
            var members = await _database().SetMembersAsync(Key(chatId));
            if (members.Length == 0) return Array.Empty<InlineBotBlock>();

            var blocks = new List<InlineBotBlock>(members.Length);
            foreach (var member in members)
            {
                var block = Decode(member);
                if (block != null)
                {
                    blocks.Add(block);
                }
                else
                {
                    LogHelper.Error($"Ignoring unreadable inline bot block '{member}' in {chatId}");
                }
            }
            return blocks;
        }

        public Task<long> CountAsync(long chatId) => _database().SetLengthAsync(Key(chatId));

        public Task<bool> AddAsync(long chatId, InlineBotBlock block) =>
            _database().SetAddAsync(Key(chatId), Encode(block));

        public Task<bool> RemoveAsync(long chatId, InlineBotBlock block) =>
            _database().SetRemoveAsync(Key(chatId), Encode(block));

        private static RedisKey Key(long chatId) => $"chat:{chatId}:blockedinline";

        private static RedisValue Encode(InlineBotBlock block)
        {
            if (block == null) throw new ArgumentNullException(nameof(block));
            var prefix = block.Kind == InlineBotBlockKind.Pattern ? PatternPrefix : UsernamePrefix;
            return prefix + block.Value;
        }

        private static InlineBotBlock Decode(RedisValue member)
        {
            var text = (string)member;
            if (text == null) return null;
            if (text.StartsWith(PatternPrefix, StringComparison.Ordinal))
                return Create(InlineBotBlockKind.Pattern, text.Substring(PatternPrefix.Length));
            if (text.StartsWith(UsernamePrefix, StringComparison.Ordinal))
                return Create(InlineBotBlockKind.Username, text.Substring(UsernamePrefix.Length));
            return null;
        }

        private static InlineBotBlock Create(InlineBotBlockKind kind, string value) =>
            value.Length == 0 ? null : new InlineBotBlock(kind, value);
    }
}
