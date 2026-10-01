using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using StackExchange.Redis;

namespace Enforcer5.Tests.ChannelPostTests
{
    [TestClass]
    public class RedisChannelPostRepositoryTests
    {
        private const long ChatId = -1001234567890;

        private readonly Dictionary<string, Dictionary<RedisValue, RedisValue>> _hashes = new();
        private readonly Dictionary<string, (RedisValue Value, TimeSpan? Ttl)> _strings = new();
        private IChannelPostRepository _repository;

        private Dictionary<RedisValue, RedisValue> Hash(RedisKey key)
        {
            if (!_hashes.TryGetValue(key.ToString(), out var hash)) _hashes[key.ToString()] = hash = new();
            return hash;
        }

        [TestInitialize]
        public void SetUp()
        {
            var db = new Mock<IDatabaseAsync>(MockBehavior.Strict);
            db.Setup(d => d.HashGetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue f, CommandFlags _) => Task.FromResult(Hash(k).TryGetValue(f, out var v) ? v : RedisValue.Null));
            db.Setup(d => d.HashExistsAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue f, CommandFlags _) => Task.FromResult(Hash(k).ContainsKey(f)));
            db.Setup(d => d.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, CommandFlags _) => Task.FromResult(Hash(k).Select(e => new HashEntry(e.Key, e.Value)).ToArray()));
            db.Setup(d => d.HashLengthAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, CommandFlags _) => Task.FromResult((long)Hash(k).Count));
            db.Setup(d => d.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue f, RedisValue v, When when, CommandFlags _) =>
                {
                    var exists = Hash(k).ContainsKey(f);
                    if (when == When.NotExists && exists) return Task.FromResult(false);
                    Hash(k)[f] = v;
                    return Task.FromResult(!exists);
                });
            db.Setup(d => d.HashDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue f, CommandFlags _) => Task.FromResult(Hash(k).Remove(f)));
            db.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, CommandFlags _) => Task.FromResult(_strings.TryGetValue(k.ToString(), out var s) ? s.Value : RedisValue.Null));
            db.Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue v, TimeSpan? ttl, When _, CommandFlags __) =>
                {
                    _strings[k.ToString()] = (v, ttl);
                    return Task.FromResult(true);
                });

            _repository = new RedisChannelPostRepository(() => db.Object);
        }

        [TestMethod]
        public async Task NewChat_IsOffWithNothingCached()
        {
            Assert.IsFalse(await _repository.IsEnabledAsync(ChatId));
            Assert.AreEqual(new ChannelPostPolicy(false, false, null), await _repository.GetPolicyAsync(ChatId, -1009));
        }

        [TestMethod]
        public async Task Policy_ReflectsSettingAllowlistAndLinkedChannel()
        {
            await _repository.SetEnabledAsync(ChatId, true);
            await _repository.AllowAsync(ChatId, new AllowedChannel(-1008, "News"));
            await _repository.SetLinkedChannelAsync(ChatId, -1005);

            Assert.AreEqual(new ChannelPostPolicy(true, true, -1005), await _repository.GetPolicyAsync(ChatId, -1008));
            Assert.AreEqual(new ChannelPostPolicy(true, false, -1005), await _repository.GetPolicyAsync(ChatId, -1009));

            await _repository.SetEnabledAsync(ChatId, false);
            Assert.IsFalse(await _repository.IsEnabledAsync(ChatId));
        }

        [TestMethod]
        public async Task NoLinkedChannel_IsCachedAsZero()
        {
            await _repository.SetLinkedChannelAsync(ChatId, 0);
            Assert.AreEqual(0L, (await _repository.GetPolicyAsync(ChatId, -1009)).LinkedChannelId);
        }

        [TestMethod]
        public async Task LinkedChannelCache_ExpiresAfterTenMinutes()
        {
            await _repository.SetLinkedChannelAsync(ChatId, -1005);
            Assert.AreEqual(TimeSpan.FromMinutes(10), _strings[$"chat:{ChatId}:linkedchannel"].Ttl);
        }

        [TestMethod]
        public async Task Allowlist_AddListRemove()
        {
            Assert.IsTrue(await _repository.AllowAsync(ChatId, new AllowedChannel(-1008, "News")));
            Assert.IsFalse(await _repository.AllowAsync(ChatId, new AllowedChannel(-1008, "Renamed")), "already allowed");
            Assert.IsTrue(await _repository.AllowAsync(ChatId, new AllowedChannel(-1007, null)));
            Assert.AreEqual(2L, await _repository.CountAllowedAsync(ChatId));

            CollectionAssert.AreEquivalent(
                new[] { new AllowedChannel(-1008, "News"), new AllowedChannel(-1007, "") },
                (await _repository.GetAllowedAsync(ChatId)).ToArray());

            Assert.IsTrue(await _repository.DisallowAsync(ChatId, -1008));
            Assert.IsFalse(await _repository.DisallowAsync(ChatId, -1008));
            Assert.AreEqual(1L, await _repository.CountAllowedAsync(ChatId));
        }

        [TestMethod]
        public async Task StorageFormat_IsStable()
        {
            // Stored data depends on this layout; changing it needs a migration.
            await _repository.SetEnabledAsync(ChatId, true);
            await _repository.AllowAsync(ChatId, new AllowedChannel(-1008, "News"));
            await _repository.SetLinkedChannelAsync(ChatId, -1005);

            Assert.AreEqual("yes", (string)_hashes[$"chat:{ChatId}:channelposts"]["enabled"]);
            Assert.AreEqual("News", (string)_hashes[$"chat:{ChatId}:allowedchannels"][-1008]);
            Assert.AreEqual(-1005L, (long)_strings[$"chat:{ChatId}:linkedchannel"].Value);
        }

        [TestMethod]
        public async Task UnreadableAllowlistEntry_IsSkipped()
        {
            await _repository.AllowAsync(ChatId, new AllowedChannel(-1008, "News"));
            Hash($"chat:{ChatId}:allowedchannels")["garbage"] = "x";
            Assert.AreEqual(1, (await _repository.GetAllowedAsync(ChatId)).Count);
        }

        [TestMethod]
        public void CompositionRoot_UsesRedis()
        {
            Assert.IsInstanceOfType(Repositories.ChannelPosts, typeof(RedisChannelPostRepository));
        }
    }
}
