using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Moq;
using StackExchange.Redis;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Real strings, sets and sorted sets for the harness's Redis stand-in, with expiry on the
    /// simulated clock: counters, journals and TTL'd state (the enforcement queue, the slow lane,
    /// the flood counter) behave as they do in Redis. Hashes live in <see cref="FakeRedisHashes"/>.
    /// Anything not set up here stays a loose mock: reads find nothing, writes are dropped.
    /// </summary>
    internal sealed class FakeRedisKeys
    {
        private readonly object _lock = new();
        private readonly FakeClock _clock;
        private readonly FakeRedisHashes _hashes;
        private readonly Dictionary<string, RedisValue> _strings = new();
        private readonly Dictionary<string, HashSet<RedisValue>> _sets = new();
        private readonly Dictionary<string, Dictionary<RedisValue, double>> _sortedSets = new();
        private readonly Dictionary<string, DateTimeOffset> _expiry = new();

        internal FakeRedisKeys(FakeClock clock, FakeRedisHashes hashes)
        {
            _clock = clock;
            _hashes = hashes;
        }

        internal void Install(Mock<IDatabase> redis)
        {
            // Strings
            redis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, CommandFlags _) => GetString(k));
            redis.Setup(r => r.StringGet(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, CommandFlags _) => GetString(k));
            redis.Setup(r => r.StringGetAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey[] ks, CommandFlags _) => ks.Select(GetString).ToArray());
            redis.Setup(r => r.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>()))
                .ReturnsAsync((RedisKey k, RedisValue v, TimeSpan? ttl, When when) => SetString(k, v, ttl, false, when));
            redis.Setup(r => r.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, RedisValue v, TimeSpan? ttl, When when, CommandFlags _) => SetString(k, v, ttl, false, when));
            redis.Setup(r => r.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, RedisValue v, TimeSpan? ttl, bool keepTtl, When when, CommandFlags _) => SetString(k, v, ttl, keepTtl, when));
            redis.Setup(r => r.StringSetAsync(It.IsAny<KeyValuePair<RedisKey, RedisValue>[]>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((KeyValuePair<RedisKey, RedisValue>[] pairs, When when, CommandFlags _) =>
                {
                    foreach (var p in pairs) SetString(p.Key, p.Value, null, false, when);
                    return true;
                });
            redis.Setup(r => r.StringIncrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, long by, CommandFlags _) => Increment(k, by));
            redis.Setup(r => r.StringDecrementAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, long by, CommandFlags _) => Increment(k, -by));

            // Keys
            redis.Setup(r => r.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, CommandFlags _) => Delete(k));
            redis.Setup(r => r.KeyDeleteAsync(It.IsAny<RedisKey[]>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey[] ks, CommandFlags _) => (long)ks.Count(Delete));
            redis.Setup(r => r.KeyExistsAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, CommandFlags _) => Exists(k));
            redis.Setup(r => r.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, TimeSpan? ttl, CommandFlags _) => Expire(k, ttl));
            redis.Setup(r => r.KeyExpireAsync(It.IsAny<RedisKey>(), It.IsAny<TimeSpan?>(), It.IsAny<ExpireWhen>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, TimeSpan? ttl, ExpireWhen _, CommandFlags __) => Expire(k, ttl));
            redis.Setup(r => r.KeyTimeToLiveAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, CommandFlags _) => TimeToLive(k));

            // Sets
            redis.Setup(r => r.SetAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, RedisValue v, CommandFlags _) => Locked(() => Set(k, true).Add(v)));
            redis.Setup(r => r.SetRemoveAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, RedisValue v, CommandFlags _) => Locked(() => Set(k, false)?.Remove(v) ?? false));
            redis.Setup(r => r.SetContainsAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, RedisValue v, CommandFlags _) => Locked(() => Set(k, false)?.Contains(v) ?? false));
            redis.Setup(r => r.SetMembersAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, CommandFlags _) => Locked(() => Set(k, false)?.ToArray() ?? Array.Empty<RedisValue>()));
            redis.Setup(r => r.SetLengthAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, CommandFlags _) => Locked(() => (long)(Set(k, false)?.Count ?? 0)));

            // Sorted sets
            redis.Setup(r => r.SortedSetAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<double>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, RedisValue v, double s, CommandFlags _) => ZAdd(k, v, s));
            redis.Setup(r => r.SortedSetAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<double>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, RedisValue v, double s, When _, CommandFlags __) => ZAdd(k, v, s));
            redis.Setup(r => r.SortedSetAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<double>(), It.IsAny<SortedSetWhen>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, RedisValue v, double s, SortedSetWhen _, CommandFlags __) => ZAdd(k, v, s));
            redis.Setup(r => r.SortedSetRemoveAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, RedisValue v, CommandFlags _) => Locked(() => SortedSet(k, false)?.Remove(v) ?? false));
            redis.Setup(r => r.SortedSetRemoveAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, RedisValue[] vs, CommandFlags _) => Locked(() => (long)vs.Count(v => SortedSet(k, false)?.Remove(v) ?? false)));
            redis.Setup(r => r.SortedSetRangeByScoreAsync(It.IsAny<RedisKey>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<Exclude>(), It.IsAny<Order>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, double min, double max, Exclude ex, Order order, long skip, long take, CommandFlags _) =>
                    ZRange(k, min, max, order, skip, take).Select(e => e.Element).ToArray());
            redis.Setup(r => r.SortedSetRangeByScoreWithScoresAsync(It.IsAny<RedisKey>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<Exclude>(), It.IsAny<Order>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, double min, double max, Exclude ex, Order order, long skip, long take, CommandFlags _) =>
                    ZRange(k, min, max, order, skip, take));
            redis.Setup(r => r.SortedSetRemoveRangeByScoreAsync(It.IsAny<RedisKey>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<Exclude>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, double min, double max, Exclude _, CommandFlags __) => Locked(() =>
                {
                    var set = SortedSet(k, false);
                    if (set == null) return 0L;
                    var gone = set.Where(p => p.Value >= min && p.Value <= max).Select(p => p.Key).ToList();
                    foreach (var g in gone) set.Remove(g);
                    return (long)gone.Count;
                }));
            redis.Setup(r => r.SortedSetLengthAsync(It.IsAny<RedisKey>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<Exclude>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, double min, double max, Exclude _, CommandFlags __) => Locked(() =>
                    (long)(SortedSet(k, false)?.Count(p => p.Value >= min && p.Value <= max) ?? 0)));
            redis.Setup(r => r.SortedSetScoreAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, RedisValue v, CommandFlags _) => Locked(() =>
                    SortedSet(k, false) is { } set && set.TryGetValue(v, out var s) ? s : (double?)null));
        }

        /// <summary>A string's value, null when it is not set or has expired.</summary>
        internal string this[string key]
        {
            get { var v = GetString(key); return v.IsNull ? null : v.ToString(); }
            set
            {
                if (value == null) Delete(key);
                else SetString(key, value, null, false, When.Always);
            }
        }

        /// <summary>A copy of a set's members.</summary>
        internal IReadOnlyCollection<string> Members(string key) =>
            Locked(() => Set(key, false)?.Select(v => v.ToString()).ToList() ?? new List<string>());

        /// <summary>Adds to a set, to seed a scenario.</summary>
        internal void AddToSet(string key, string value) => Locked(() => Set(key, true).Add(value));

        /// <summary>A copy of a sorted set, by score.</summary>
        internal IReadOnlyList<(string Member, double Score)> SortedSetEntries(string key) =>
            Locked(() => SortedSet(key, false)?.OrderBy(p => p.Value).Select(p => (p.Key.ToString(), p.Value)).ToList()
                         ?? new List<(string, double)>());

        /// <summary>Every live key of the kinds kept here.</summary>
        internal IReadOnlyCollection<string> Keys => Locked(() =>
        {
            foreach (var key in _expiry.Keys.ToList()) Expired(key);
            return _strings.Keys.Concat(_sets.Where(s => s.Value.Count > 0).Select(s => s.Key))
                .Concat(_sortedSets.Where(s => s.Value.Count > 0).Select(s => s.Key)).ToList();
        });

        private T Locked<T>(Func<T> f)
        {
            lock (_lock) return f();
        }

        // Called with the lock held. Drops the key when its time is up.
        private bool Expired(string key)
        {
            if (!_expiry.TryGetValue(key, out var at) || at > _clock.GetUtcNow()) return false;
            _expiry.Remove(key);
            _strings.Remove(key);
            _sets.Remove(key);
            _sortedSets.Remove(key);
            _hashes.DeleteKey(key);
            return true;
        }

        private RedisValue GetString(RedisKey key) => Locked(() =>
        {
            Expired(key);
            return _strings.TryGetValue(key, out var v) ? v : RedisValue.Null;
        });

        private bool SetString(RedisKey key, RedisValue value, TimeSpan? ttl, bool keepTtl, When when) => Locked(() =>
        {
            Expired(key);
            var exists = _strings.ContainsKey(key);
            if (when == When.NotExists && exists) return false;
            if (when == When.Exists && !exists) return false;
            _strings[key] = value;
            if (ttl.HasValue) _expiry[key] = _clock.GetUtcNow() + ttl.Value;
            else if (!keepTtl) _expiry.Remove(key);
            return true;
        });

        private long Increment(RedisKey key, long by) => Locked(() =>
        {
            Expired(key);
            var current = _strings.TryGetValue(key, out var v) ? long.Parse(v.ToString(), CultureInfo.InvariantCulture) : 0;
            _strings[key] = current + by;
            return current + by;
        });

        private bool Delete(RedisKey key) => Locked(() =>
        {
            var expired = Expired(key);
            _expiry.Remove(key);
            var removed = _strings.Remove(key) | _sets.Remove(key) | _sortedSets.Remove(key) | _hashes.DeleteKey(key);
            return removed && !expired;
        });

        private bool Exists(RedisKey key) => Locked(() =>
            !Expired(key) && (_strings.ContainsKey(key) || _sets.ContainsKey(key) || _sortedSets.ContainsKey(key) || _hashes.Exists(key)));

        private bool Expire(RedisKey key, TimeSpan? ttl) => Locked(() =>
        {
            if (Expired(key)) return false;
            if (!(_strings.ContainsKey(key) || _sets.ContainsKey(key) || _sortedSets.ContainsKey(key) || _hashes.Exists(key))) return false;
            if (ttl.HasValue) _expiry[key] = _clock.GetUtcNow() + ttl.Value;
            else _expiry.Remove(key);
            return true;
        });

        private TimeSpan? TimeToLive(RedisKey key) => Locked(() =>
        {
            Expired(key);
            return _expiry.TryGetValue(key, out var at) ? at - _clock.GetUtcNow() : (TimeSpan?)null;
        });

        private HashSet<RedisValue> Set(RedisKey key, bool create)
        {
            Expired(key);
            if (!_sets.TryGetValue(key, out var set) && create) _sets[key] = set = new HashSet<RedisValue>();
            return set;
        }

        private Dictionary<RedisValue, double> SortedSet(RedisKey key, bool create)
        {
            Expired(key);
            if (!_sortedSets.TryGetValue(key, out var set) && create) _sortedSets[key] = set = new Dictionary<RedisValue, double>();
            return set;
        }

        private bool ZAdd(RedisKey key, RedisValue value, double score) => Locked(() =>
        {
            var set = SortedSet(key, true);
            var added = !set.ContainsKey(value);
            set[value] = score;
            return added;
        });

        private SortedSetEntry[] ZRange(RedisKey key, double min, double max, Order order, long skip, long take) => Locked(() =>
        {
            var set = SortedSet(key, false);
            if (set == null) return Array.Empty<SortedSetEntry>();
            var range = set.Where(p => p.Value >= min && p.Value <= max);
            range = order == Order.Ascending
                ? range.OrderBy(p => p.Value).ThenBy(p => p.Key.ToString(), StringComparer.Ordinal)
                : range.OrderByDescending(p => p.Value).ThenByDescending(p => p.Key.ToString(), StringComparer.Ordinal);
            var list = range.Skip((int)skip);
            if (take >= 0) list = list.Take((int)take);
            return list.Select(p => new SortedSetEntry(p.Key, p.Value)).ToArray();
        });
    }
}
