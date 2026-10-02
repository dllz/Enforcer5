using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using StackExchange.Redis;

namespace Enforcer5.Tests.Mutes
{
    [TestClass]
    public class RedisMuteRepositoryTests
    {
        private const long ChatId = -1001234567890;
        private static readonly DateTime Now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

        private readonly Dictionary<string, HashSet<RedisValue>> _sets = new();
        private readonly Dictionary<string, Dictionary<RedisValue, RedisValue>> _hashes = new();
        private IMuteRepository _repository;

        private HashSet<RedisValue> Set(RedisKey key)
        {
            if (!_sets.TryGetValue(key.ToString(), out var set)) _sets[key.ToString()] = set = new();
            return set;
        }

        private Dictionary<RedisValue, RedisValue> Hash(RedisKey key)
        {
            if (!_hashes.TryGetValue(key.ToString(), out var hash)) _hashes[key.ToString()] = hash = new();
            return hash;
        }

        [TestInitialize]
        public void SetUp()
        {
            var db = new Mock<IDatabaseAsync>(MockBehavior.Strict);
            db.Setup(d => d.SetAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue v, CommandFlags _) => Task.FromResult(Set(k).Add(v)));
            db.Setup(d => d.SetRemoveAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue v, CommandFlags _) => Task.FromResult(Set(k).Remove(v)));
            db.Setup(d => d.SetContainsAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue v, CommandFlags _) => Task.FromResult(Set(k).Contains(v)));
            db.Setup(d => d.SetMembersAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, CommandFlags _) => Task.FromResult(Set(k).ToArray()));
            db.Setup(d => d.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue f, RedisValue v, When _, CommandFlags __) =>
                {
                    var added = !Hash(k).ContainsKey(f);
                    Hash(k)[f] = v;
                    return Task.FromResult(added);
                });
            db.Setup(d => d.HashDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue f, CommandFlags _) => Task.FromResult(Hash(k).Remove(f)));
            db.Setup(d => d.HashDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue[]>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue[] fs, CommandFlags _) => Task.FromResult((long)fs.Count(f => Hash(k).Remove(f))));
            db.Setup(d => d.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, CommandFlags _) => Task.FromResult(Hash(k).Select(e => new HashEntry(e.Key, e.Value)).ToArray()));

            _repository = new RedisMuteRepository(() => db.Object);
        }

        private async Task<(IReadOnlyList<long> Muted, IReadOnlyList<long> Joiners, IReadOnlyList<TimedRestriction> Temp)> All() =>
            (await _repository.GetMutedAsync(ChatId), await _repository.GetMutedJoinersAsync(ChatId),
                await _repository.GetTempMutedAsync(ChatId, Now));

        [TestMethod]
        public async Task NewChat_HasNoMutes()
        {
            var (muted, joiners, temp) = await All();
            Assert.AreEqual(0, muted.Count);
            Assert.AreEqual(0, joiners.Count);
            Assert.AreEqual(0, temp.Count);
        }

        [TestMethod]
        public async Task Mute_IsListed()
        {
            await _repository.MarkMutedAsync(ChatId, 1);
            await _repository.MarkMutedAsync(ChatId, 2);
            CollectionAssert.AreEquivalent(new long[] { 1, 2 }, (await _repository.GetMutedAsync(ChatId)).ToArray());
        }

        [TestMethod]
        public async Task Tempmute_IsListedWithItsEnd()
        {
            var until = Now.AddHours(2);
            await _repository.MarkTempMutedAsync(ChatId, 1, until);
            var temp = await _repository.GetTempMutedAsync(ChatId, Now);
            Assert.AreEqual(1, temp.Count);
            Assert.AreEqual(new TimedRestriction(1, until), temp[0]);
        }

        [TestMethod]
        public async Task EndedTempmutes_AreDroppedAndPruned()
        {
            await _repository.MarkTempMutedAsync(ChatId, 1, Now.AddMinutes(-1));
            await _repository.MarkTempMutedAsync(ChatId, 2, Now);
            await _repository.MarkTempMutedAsync(ChatId, 3, Now.AddMinutes(1));
            Hash($"chat:{ChatId}:tempmuted")["garbage"] = "x";

            var temp = await _repository.GetTempMutedAsync(ChatId, Now);
            CollectionAssert.AreEqual(new long[] { 3 }, temp.Select(t => t.UserId).ToArray());
            CollectionAssert.AreEquivalent(new RedisValue[] { 3 }, Hash($"chat:{ChatId}:tempmuted").Keys.ToArray(),
                "nothing else removes ended entries, so reading must");
        }

        [TestMethod]
        public async Task LatestMute_Wins()
        {
            await _repository.MarkJoinerMutedAsync(ChatId, 1);
            await _repository.MarkTempMutedAsync(ChatId, 1, Now.AddHours(1));
            var (muted, joiners, temp) = await All();
            Assert.AreEqual(0, muted.Count);
            Assert.AreEqual(0, joiners.Count, "a tempmute replaces the join mute");
            Assert.AreEqual(1, temp.Count);

            await _repository.MarkMutedAsync(ChatId, 1);
            (muted, joiners, temp) = await All();
            CollectionAssert.AreEqual(new long[] { 1 }, muted.ToArray());
            Assert.AreEqual(0, temp.Count, "a mute replaces the tempmute");

            await _repository.MarkTempMutedAsync(ChatId, 1, Now.AddHours(1));
            (muted, _, temp) = await All();
            Assert.AreEqual(0, muted.Count, "and the other way round");
            Assert.AreEqual(1, temp.Count);
        }

        [TestMethod]
        public async Task JoinMute_DoesNotDemoteAnAdminsMute()
        {
            // Otherwise /unmutenewjoiners would unmute someone an admin muted, who left and came back.
            await _repository.MarkMutedAsync(ChatId, 1);
            await _repository.MarkJoinerMutedAsync(ChatId, 1);
            var (muted, joiners, _) = await All();
            CollectionAssert.AreEqual(new long[] { 1 }, muted.ToArray());
            Assert.AreEqual(0, joiners.Count);
        }

        [TestMethod]
        public async Task JoinMute_ReplacesATempmute()
        {
            // Mute On Join mutes with no end, overriding the tempmute in Telegram too.
            await _repository.MarkTempMutedAsync(ChatId, 1, Now.AddHours(1));
            await _repository.MarkJoinerMutedAsync(ChatId, 1);
            var (_, joiners, temp) = await All();
            CollectionAssert.AreEqual(new long[] { 1 }, joiners.ToArray());
            Assert.AreEqual(0, temp.Count);
        }

        [TestMethod]
        public async Task Clear_ForgetsEveryKindOfMute()
        {
            await _repository.MarkMutedAsync(ChatId, 1);
            await _repository.MarkJoinerMutedAsync(ChatId, 2);
            await _repository.MarkTempMutedAsync(ChatId, 3, Now.AddHours(1));
            await _repository.MarkMutedAsync(ChatId, 4);

            foreach (var user in new long[] { 1, 2, 3 }) await _repository.ClearAsync(ChatId, user);

            var (muted, joiners, temp) = await All();
            CollectionAssert.AreEqual(new long[] { 4 }, muted.ToArray());
            Assert.AreEqual(0, joiners.Count);
            Assert.AreEqual(0, temp.Count);
        }

        [TestMethod]
        public async Task Chats_AreSeparate()
        {
            await _repository.MarkMutedAsync(ChatId, 1);
            Assert.AreEqual(0, (await _repository.GetMutedAsync(-100999)).Count);
        }

        [TestMethod]
        public async Task UnreadableSetEntry_IsSkipped()
        {
            await _repository.MarkMutedAsync(ChatId, 1);
            Set($"chat:{ChatId}:muted").Add("garbage");
            CollectionAssert.AreEqual(new long[] { 1 }, (await _repository.GetMutedAsync(ChatId)).ToArray());
        }

        [TestMethod]
        public async Task StorageFormat_IsStable()
        {
            // These keys predate the repository and hold live data; changing them needs a migration.
            await _repository.MarkMutedAsync(ChatId, 1);
            await _repository.MarkJoinerMutedAsync(ChatId, 2);
            await _repository.MarkTempMutedAsync(ChatId, 3, Now.AddHours(1));

            Assert.IsTrue(_sets[$"chat:{ChatId}:muted"].Contains(1));
            Assert.IsTrue(_sets[$"chat:{ChatId}:mutedJoiners"].Contains(2));
            Assert.AreEqual(new DateTimeOffset(Now.AddHours(1)).ToUnixTimeSeconds(), (long)_hashes[$"chat:{ChatId}:tempmuted"][3]);
        }

        [TestMethod]
        public void CompositionRoot_UsesRedis()
        {
            Assert.IsInstanceOfType(Repositories.Mutes, typeof(RedisMuteRepository));
            Assert.IsInstanceOfType(Repositories.Tempbans, typeof(RedisTempbanRepository));
        }
    }
}
