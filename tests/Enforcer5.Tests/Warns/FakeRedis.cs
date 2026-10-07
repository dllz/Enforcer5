using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StackExchange.Redis;
using Moq;

namespace Enforcer5.Tests.Warns
{
    /// <summary>
    /// An in-memory stand-in for the string, hash, list and key commands the warn repositories use.
    /// Expiry is recorded, not enforced.
    /// </summary>
    internal sealed class FakeRedis
    {
        public readonly Dictionary<string, (RedisValue Value, TimeSpan? Ttl)> Strings = new();
        public readonly Dictionary<string, Dictionary<RedisValue, RedisValue>> Hashes = new();
        public readonly Dictionary<string, List<RedisValue>> Lists = new();
        public readonly Mock<IDatabaseAsync> Mock = new(MockBehavior.Strict);

        public IDatabaseAsync Database => Mock.Object;

        public Dictionary<RedisValue, RedisValue> Hash(RedisKey key)
        {
            if (!Hashes.TryGetValue(key.ToString(), out var hash)) Hashes[key.ToString()] = hash = new();
            return hash;
        }

        public List<RedisValue> List(RedisKey key)
        {
            if (!Lists.TryGetValue(key.ToString(), out var list)) Lists[key.ToString()] = list = new();
            return list;
        }

        public IEnumerable<string> Keys =>
            Strings.Keys.Concat(Hashes.Where(h => h.Value.Count > 0).Select(h => h.Key))
                .Concat(Lists.Where(l => l.Value.Count > 0).Select(l => l.Key));

        public FakeRedis()
        {
            Mock.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, CommandFlags _) => Task.FromResult(Strings.TryGetValue(k.ToString(), out var s) ? s.Value : RedisValue.Null));
            Mock.Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue v, TimeSpan? ttl, When _, CommandFlags __) =>
                {
                    Strings[k.ToString()] = (v, ttl);
                    return Task.FromResult(true);
                });
            Mock.Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, CommandFlags _) => Task.FromResult(
                    Strings.Remove(k.ToString()) | Hashes.Remove(k.ToString()) | Lists.Remove(k.ToString())));

            Mock.Setup(d => d.HashGetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue f, CommandFlags _) => Task.FromResult(Hash(k).TryGetValue(f, out var v) ? v : RedisValue.Null));
            Mock.Setup(d => d.HashDecrementAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue f, long by, CommandFlags _) =>
                {
                    var current = Hash(k).TryGetValue(f, out var v) ? (long)v : 0;
                    Hash(k)[f] = current - by;
                    return Task.FromResult(current - by);
                });
            Mock.Setup(d => d.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue f, RedisValue v, When _, CommandFlags __) =>
                {
                    var added = !Hash(k).ContainsKey(f);
                    Hash(k)[f] = v;
                    return Task.FromResult(added);
                });
            Mock.Setup(d => d.HashDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue f, CommandFlags _) => Task.FromResult(Hash(k).Remove(f)));

            Mock.Setup(d => d.ListRangeAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, long start, long stop, CommandFlags _) =>
                {
                    var list = List(k);
                    var end = stop < 0 ? list.Count - 1 : Math.Min(stop, list.Count - 1);
                    return Task.FromResult(list.Skip((int)start).Take((int)Math.Max(0, end - start + 1)).ToArray());
                });
            Mock.Setup(d => d.ListRemoveAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue v, long _, CommandFlags __) => Task.FromResult((long)List(k).RemoveAll(x => x == v)));
            Mock.Setup(d => d.ListLeftPushAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue v, When _, CommandFlags __) =>
                {
                    List(k).Insert(0, v);
                    return Task.FromResult((long)List(k).Count);
                });
            Mock.Setup(d => d.ListTrimAsync(It.IsAny<RedisKey>(), It.IsAny<long>(), It.IsAny<long>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, long start, long stop, CommandFlags _) =>
                {
                    var list = List(k);
                    var kept = list.Skip((int)start).Take((int)(stop - start + 1)).ToList();
                    list.Clear();
                    list.AddRange(kept);
                    return Task.CompletedTask;
                });
        }
    }
}
