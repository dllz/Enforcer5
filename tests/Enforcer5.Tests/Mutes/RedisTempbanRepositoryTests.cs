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
    public class RedisTempbanRepositoryTests
    {
        private const long ChatId = -1001234567890;
        private const long OtherChat = -1009876543210;
        private static readonly DateTime Now = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);

        private readonly Dictionary<string, HashSet<RedisValue>> _sets = new();
        private readonly Dictionary<string, Dictionary<RedisValue, RedisValue>> _hashes = new();
        private readonly Dictionary<string, RedisValue> _strings = new();
        private Mock<IDatabaseAsync> _db;

        private static long Unix(DateTime time) => new DateTimeOffset(time).ToUnixTimeSeconds();

        private HashSet<RedisValue> Set(string key)
        {
            if (!_sets.TryGetValue(key, out var set)) _sets[key] = set = new();
            return set;
        }

        private Dictionary<RedisValue, RedisValue> Hash(string key)
        {
            if (!_hashes.TryGetValue(key, out var hash)) _hashes[key] = hash = new();
            return hash;
        }

        [TestInitialize]
        public void SetUp()
        {
            _db = new Mock<IDatabaseAsync>(MockBehavior.Strict);
            _db.Setup(d => d.SetMembersAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, CommandFlags _) => Task.FromResult(Set(k.ToString()).ToArray()));
            _db.Setup(d => d.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, CommandFlags _) => Task.FromResult(Hash(k.ToString()).Select(e => new HashEntry(e.Key, e.Value)).ToArray()));
            _db.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, CommandFlags _) => Task.FromResult(_strings.TryGetValue(k.ToString(), out var v) ? v : RedisValue.Null));
        }

        private ITempbanRepository Repository(string index = "tempbanned") => new RedisTempbanRepository(() => _db.Object, index);

        /// <summary>Writes a tempban the way Commands.Tempban does.</summary>
        private void Tempban(long chatId, long userId, DateTime until, string index = "tempbanned", bool perChat = true, bool global = true)
        {
            if (perChat)
            {
                Set($"chat:{chatId}:{index}").Add(userId);
                _strings[$"chat:{chatId}:tempbanned:{userId}"] = Unix(until);
            }
            if (global) Hash(index)[Unix(until)] = $"{chatId}:{userId}:Some Name:Some Group";
        }

        private async Task<TimedRestriction[]> Active(string index = "tempbanned") =>
            (await Repository(index).GetActiveAsync(ChatId, Now)).ToArray();

        [TestMethod]
        public async Task NoTempbans_IsEmpty()
        {
            Assert.AreEqual(0, (await Active()).Length);
        }

        [TestMethod]
        public async Task RunningTempbans_AreListedSoonestFirst()
        {
            Tempban(ChatId, 1, Now.AddDays(1));
            Tempban(ChatId, 2, Now.AddHours(1));
            CollectionAssert.AreEqual(
                new[] { new TimedRestriction(2, Now.AddHours(1)), new TimedRestriction(1, Now.AddDays(1)) },
                await Active());
        }

        [TestMethod]
        public async Task EndedTempbans_AreLeftOut()
        {
            Tempban(ChatId, 1, Now.AddMinutes(-1));
            Tempban(ChatId, 2, Now);
            Assert.AreEqual(0, (await Active()).Length);
        }

        [TestMethod]
        public async Task OtherChats_AreLeftOut()
        {
            Tempban(OtherChat, 1, Now.AddHours(1));
            Assert.AreEqual(0, (await Active()).Length);
        }

        [TestMethod]
        public async Task EitherStoreAlone_IsEnough()
        {
            // The two stores have diverged in production, so neither is trusted to be complete.
            Tempban(ChatId, 1, Now.AddHours(1), global: false);
            Tempban(ChatId, 2, Now.AddHours(2), perChat: false);
            CollectionAssert.AreEquivalent(new long[] { 1, 2 }, (await Active()).Select(t => t.UserId).ToArray());
        }

        [TestMethod]
        public async Task PerChatExpiry_WinsOverALeakedGlobalEntry()
        {
            // A re-tempban rewrites the per-chat expiry; an older global entry can survive it.
            Tempban(ChatId, 1, Now.AddDays(3), perChat: false);
            Tempban(ChatId, 1, Now.AddHours(1));
            CollectionAssert.AreEqual(new[] { new TimedRestriction(1, Now.AddHours(1)) }, await Active());
        }

        [TestMethod]
        public async Task SetMemberWithoutExpiry_IsNotATempban()
        {
            // The expiry key lapses with the tempban; the set entry is only removed by the checker.
            Set($"chat:{ChatId}:tempbanned").Add(1);
            Assert.AreEqual(0, (await Active()).Length);
        }

        [TestMethod]
        public async Task Premium_ReadsItsOwnIndex()
        {
            Tempban(ChatId, 1, Now.AddHours(1), index: "tempbannedPremium");
            Assert.AreEqual(0, (await Active("tempbanned")).Length);
            Assert.AreEqual(1, (await Active("tempbannedPremium")).Length);
        }

        [TestMethod]
        public async Task UnreadableEntries_AreSkipped()
        {
            Tempban(ChatId, 1, Now.AddHours(1));
            Set($"chat:{ChatId}:tempbanned").Add("garbage");
            Hash("tempbanned")["not-a-time"] = $"{ChatId}:2:x:y";
            Hash("tempbanned")[Unix(Now.AddHours(2))] = "garbage";
            Hash("tempbanned")[long.MaxValue] = $"{ChatId}:3:x:y";
            CollectionAssert.AreEqual(new long[] { 1 }, (await Active()).Select(t => t.UserId).ToArray());
        }

        [DataTestMethod]
        [DataRow("-100123:42:Name:Group", -100123L, 42L, true)]
        [DataRow("-100123:42", -100123L, 42L, true)]
        [DataRow("-100123:42:Name: with: colons:Group", -100123L, 42L, true)]
        [DataRow("-100123", 0L, 0L, false)]
        [DataRow("garbage:value", 0L, 0L, false)]
        [DataRow(null, 0L, 0L, false)]
        public void GlobalEntry_IsParsed(string value, long chatId, long userId, bool ok)
        {
            Assert.AreEqual(ok, RedisTempbanRepository.TryParseEntry(value, out var parsedChat, out var parsedUser));
            if (!ok) return;
            Assert.AreEqual(chatId, parsedChat);
            Assert.AreEqual(userId, parsedUser);
        }
    }
}
