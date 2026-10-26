using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Moq;
using StackExchange.Redis;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Real hashes for the harness's Redis stand-in: the hash commands Enforcer uses keep their
    /// values, so counters such as warns and the tempban schedule behave as they do in Redis and a
    /// scenario can seed settings (<c>chat:{id}:warnsettings</c>, <c>chat:{id}:media</c>, ...).
    /// Everything else stays a loose mock: reads find nothing, writes are dropped.
    /// </summary>
    internal sealed class FakeRedisHashes
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, Dictionary<string, RedisValue>> _hashes = new();

        internal void Install(Mock<IDatabase> redis)
        {
            redis.Setup(r => r.HashGetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, RedisValue f, CommandFlags _) => Get(k, f));
            redis.Setup(r => r.HashGetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, RedisValue[] fs, CommandFlags _) => fs.Select(f => Get(k, f)).ToArray());
            redis.Setup(r => r.HashGet(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue f, CommandFlags _) => Get(k, f));
            redis.Setup(r => r.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, CommandFlags _) => GetAll(k));
            redis.Setup(r => r.HashExistsAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, RedisValue f, CommandFlags _) => !Get(k, f).IsNull);
            redis.Setup(r => r.HashKeysAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, CommandFlags _) => GetAll(k).Select(e => e.Name).ToArray());
            redis.Setup(r => r.HashIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, RedisValue f, long by, CommandFlags _) => Increment(k, f, by));
            redis.Setup(r => r.HashDecrementAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, RedisValue f, long by, CommandFlags _) => Increment(k, f, -by));
            redis.Setup(r => r.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, RedisValue f, RedisValue v, When when, CommandFlags _) => SetField(k, f, v, when));
            redis.Setup(r => r.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<HashEntry[]>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, HashEntry[] entries, CommandFlags _) =>
                {
                    foreach (var e in entries) SetField(k, e.Name, e.Value, When.Always);
                    return System.Threading.Tasks.Task.CompletedTask;
                });
            redis.Setup(r => r.HashDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, RedisValue f, CommandFlags _) => Delete(k, f));
        }

        /// <summary>A field's value, null when it is not set.</summary>
        internal string this[string key, string field]
        {
            get { var v = Get(key, field); return v.IsNull ? null : v.ToString(); }
            set
            {
                if (value == null) Delete(key, field);
                else SetField(key, field, value, When.Always);
            }
        }

        /// <summary>A copy of the hash, empty when there is none.</summary>
        internal IReadOnlyDictionary<string, string> All(string key) =>
            GetAll(key).ToDictionary(e => e.Name.ToString(), e => e.Value.ToString());

        /// <summary>Removes the whole hash; true when there was one.</summary>
        internal bool DeleteKey(string key)
        {
            lock (_lock) return _hashes.Remove(key);
        }

        /// <summary>True when the hash has at least one field.</summary>
        internal bool Exists(string key)
        {
            lock (_lock) return _hashes.TryGetValue(key, out var hash) && hash.Count > 0;
        }

        private RedisValue Get(RedisKey key, RedisValue field)
        {
            lock (_lock)
                return _hashes.TryGetValue(key, out var hash) && hash.TryGetValue(field, out var value) ? value : RedisValue.Null;
        }

        private HashEntry[] GetAll(RedisKey key)
        {
            lock (_lock)
                return _hashes.TryGetValue(key, out var hash)
                    ? hash.Select(p => new HashEntry(p.Key, p.Value)).ToArray()
                    : Array.Empty<HashEntry>();
        }

        private long Increment(RedisKey key, RedisValue field, long by)
        {
            lock (_lock)
            {
                var hash = Hash(key);
                var current = hash.TryGetValue(field, out var value) ? long.Parse(value.ToString(), CultureInfo.InvariantCulture) : 0;
                hash[field] = current + by;
                return current + by;
            }
        }

        private bool SetField(RedisKey key, RedisValue field, RedisValue value, When when)
        {
            lock (_lock)
            {
                var hash = Hash(key);
                var exists = hash.ContainsKey(field);
                if (when == When.NotExists && exists) return false;
                if (when == When.Exists && !exists) return false;
                hash[field] = value;
                return !exists;
            }
        }

        private bool Delete(RedisKey key, RedisValue field)
        {
            lock (_lock)
                return _hashes.TryGetValue(key, out var hash) && hash.Remove(field);
        }

        private Dictionary<string, RedisValue> Hash(RedisKey key)
        {
            if (!_hashes.TryGetValue(key, out var hash)) _hashes[key] = hash = new Dictionary<string, RedisValue>();
            return hash;
        }
    }
}
