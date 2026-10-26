using System;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Newtonsoft.Json;
using StackExchange.Redis;

namespace Enforcer5.Data
{
    /// <summary>
    /// Redis layout: <c>slowlane:{userId}</c>, a string holding the stay as JSON, expiring once it
    /// no longer counts as a strike. Shared by both editions. Replaces the <c>spammers{userId}</c>
    /// strings of the old spam ban, which are no longer read.
    /// </summary>
    internal sealed class RedisSlowLaneRepository : ISlowLaneRepository
    {
        private readonly Func<IDatabaseAsync> _database;
        private readonly TimeSpan _strikeMemory;

        public RedisSlowLaneRepository(Func<IDatabaseAsync> database, TimeSpan strikeMemory)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _strikeMemory = strikeMemory;
        }

        internal static string Key(long userId) => $"slowlane:{userId}";

        public async Task<SlowLaneState> GetAsync(long userId)
        {
            var value = await _database().StringGetAsync(Key(userId), CommandFlags.None);
            return value.IsNullOrEmpty ? null : JsonConvert.DeserializeObject<SlowLaneState>(value.ToString());
        }

        public Task SaveAsync(long userId, SlowLaneState state)
        {
            var keep = state.QuietUntilUtc - Clock.UtcNow + _strikeMemory;
            if (keep < TimeSpan.FromMinutes(1)) keep = TimeSpan.FromMinutes(1);
            return _database().StringSetAsync(Key(userId), JsonConvert.SerializeObject(state), keep, false, When.Always, CommandFlags.None);
        }
    }
}
