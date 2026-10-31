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
            _db.Setup(d => d.HashSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue f, RedisValue v, When when, CommandFlags _) =>
                {
                    var hash = Hash(k.ToString());
                    if (when == When.NotExists && hash.ContainsKey(f)) return Task.FromResult(false);
                    hash[f] = v;
                    return Task.FromResult(true);
                });
            _db.Setup(d => d.HashDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue f, CommandFlags _) => Task.FromResult(Hash(k.ToString()).Remove(f)));
            _db.Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue v, TimeSpan? ttl, bool _, When __, CommandFlags ___) =>
                {
                    _strings[k.ToString()] = v;
                    _ttls[k.ToString()] = ttl;
                    return Task.FromResult(true);
                });
            _db.Setup(d => d.SetAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue v, CommandFlags _) => Task.FromResult(Set(k.ToString()).Add(v)));
            _db.Setup(d => d.SetRemoveAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue v, CommandFlags _) => Task.FromResult(Set(k.ToString()).Remove(v)));
            _db.Setup(d => d.SetContainsAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, RedisValue v, CommandFlags _) => Task.FromResult(Set(k.ToString()).Contains(v)));
            _db.Setup(d => d.KeyDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey k, CommandFlags _) => Task.FromResult(_strings.Remove(k.ToString())));
        }

        private readonly Dictionary<string, TimeSpan?> _ttls = new();

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

        [TestMethod]
        public async Task Schedule_WritesAllThreeKeys()
        {
            var slot = await Repository().ScheduleAsync(ChatId, 7, Now.AddHours(2), Now, "Name:Group");

            Assert.AreEqual(Unix(Now.AddHours(2)), slot);
            Assert.AreEqual($"{ChatId}:7:Name:Group", Hash("tempbanned")[slot].ToString());
            Assert.AreEqual(slot, (long)_strings[$"chat:{ChatId}:tempbanned:7"]);
            Assert.AreEqual(TimeSpan.FromHours(2), _ttls[$"chat:{ChatId}:tempbanned:7"]);
            Assert.IsTrue(Set($"chat:{ChatId}:tempbanned").Contains(7));
            CollectionAssert.AreEqual(new[] { new TimedRestriction(7, Now.AddHours(2)) }, (await Active()).ToArray());
        }

        [TestMethod]
        public async Task TwoTempbansEndingTogether_TakeTheirOwnSecond()
        {
            var first = await Repository().ScheduleAsync(ChatId, 1, Now.AddHours(1), Now, "a:b");
            var second = await Repository().ScheduleAsync(OtherChat, 2, Now.AddHours(1), Now, "a:b");

            Assert.AreEqual(first + 1, second);
            Assert.AreEqual(2, Hash("tempbanned").Count);
        }

        [TestMethod]
        public async Task Cancel_RemovesEveryEntryOfTheMember_AndNobodyElses()
        {
            // The old ban buttons compared "{chat}:{user}" with the whole stored value and never matched (#24).
            Tempban(ChatId, 42, Now.AddHours(1));
            Hash("tempbanned")[Unix(Now.AddHours(3))] = $"{ChatId}:42:Older:Entry";
            Tempban(ChatId, 421, Now.AddHours(2));
            Tempban(OtherChat, 42, Now.AddHours(4));

            await Repository().CancelAsync(ChatId, 42);

            CollectionAssert.AreEquivalent(new[] { $"{ChatId}:421:Some Name:Some Group", $"{OtherChat}:42:Some Name:Some Group" },
                Hash("tempbanned").Values.Select(v => v.ToString()).ToArray());
            Assert.IsFalse(Set($"chat:{ChatId}:tempbanned").Contains(42));
            Assert.IsFalse(_strings.ContainsKey($"chat:{ChatId}:tempbanned:42"));
            Assert.IsTrue(_strings.ContainsKey($"chat:{OtherChat}:tempbanned:42"));
        }

        [TestMethod]
        public async Task Cancel_WithoutATempban_DoesNotReadTheSchedule()
        {
            Tempban(ChatId, 1, Now.AddHours(1));

            await Repository().CancelAsync(ChatId, 2);

            _db.Verify(d => d.HashGetAllAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()), Times.Never);
            Assert.AreEqual(1, Hash("tempbanned").Count);
        }

        [TestMethod]
        public async Task Cancel_UsesTheEditionsKeys()
        {
            Tempban(ChatId, 42, Now.AddHours(1), "tempbannedPremium");
            Tempban(ChatId, 42, Now.AddHours(2), "tempbanned", perChat: false);

            await Repository("tempbannedPremium").CancelAsync(ChatId, 42);

            Assert.AreEqual(0, Hash("tempbannedPremium").Count);
            Assert.AreEqual(1, Hash("tempbanned").Count, "the other edition's schedule is its own");
        }

        [TestMethod]
        public async Task Due_ListsWhatEnded_AndRemoveForgetsIt()
        {
            Tempban(ChatId, 1, Now.AddMinutes(-1));
            Tempban(ChatId, 2, Now);
            Tempban(ChatId, 3, Now.AddMinutes(1));
            Hash("tempbanned")["not-a-time"] = $"{ChatId}:4:x:y";

            var due = (await Repository().DueAsync(Now)).OrderBy(e => e.UserId).ToArray();

            CollectionAssert.AreEqual(new long[] { 1, 2 }, due.Select(e => e.UserId).ToArray());
            await Repository().RemoveAsync(due[0]);
            Assert.IsFalse(Hash("tempbanned").ContainsKey(Unix(Now.AddMinutes(-1))));
            Assert.IsFalse(Set($"chat:{ChatId}:tempbanned").Contains(1));
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
