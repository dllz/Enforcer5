using System;
using System.Threading.Tasks;
using Enforcer5.Detection;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace Enforcer5.Data
{
    /// <summary>
    /// The posts Detection deleted, for their reports (<see cref="DeletedPost"/>): what the report quotes, and
    /// the file Show deleted post sends, after a restart. A cache, not a store: kept as long as a report lives.
    /// </summary>
    internal interface IDeletedPostRepository
    {
        Task SaveAsync(long chatId, DeletedPost post);

        /// <summary>The post, or null when it was never kept or has expired.</summary>
        Task<DeletedPost> GetAsync(long chatId, long messageId);
    }

    /// <summary>Redis layout: <c>detection:deleted:{chat}:{message}</c>, a string holding the post as JSON, expiring after <see cref="DeletedPost.Keep"/>.</summary>
    internal sealed class RedisDeletedPostRepository : IDeletedPostRepository
    {
        private readonly Func<IDatabaseAsync> _database;

        public RedisDeletedPostRepository(Func<IDatabaseAsync> database)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
        }

        internal static string Key(long chatId, long messageId) => $"detection:deleted:{chatId}:{messageId}";

        public Task SaveAsync(long chatId, DeletedPost post) =>
            _database().StringSetAsync(Key(chatId, post.MessageId), JsonConvert.SerializeObject(post), DeletedPost.Keep, false, When.Always,
                CommandFlags.None);

        public async Task<DeletedPost> GetAsync(long chatId, long messageId)
        {
            var value = await _database().StringGetAsync(Key(chatId, messageId), CommandFlags.None);
            return value.IsNullOrEmpty ? null : JsonConvert.DeserializeObject<DeletedPost>(value.ToString());
        }
    }
}
