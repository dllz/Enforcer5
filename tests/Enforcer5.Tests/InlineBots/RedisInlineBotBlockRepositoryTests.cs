using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using StackExchange.Redis;

namespace Enforcer5.Tests.InlineBots
{
    [TestClass]
    public class RedisInlineBotBlockRepositoryTests
    {
        private const long ChatId = -1001234567890;
        private const string Key = "chat:-1001234567890:blockedinline";

        private readonly Dictionary<string, HashSet<RedisValue>> _sets = new Dictionary<string, HashSet<RedisValue>>();
        private IInlineBotBlockRepository _repository;

        private HashSet<RedisValue> Set(RedisKey key)
        {
            if (!_sets.TryGetValue(key.ToString(), out var set)) _sets[key.ToString()] = set = new HashSet<RedisValue>();
            return set;
        }

        [TestInitialize]
        public void SetUp()
        {
            // An in-memory stand-in for the four set commands the repository uses.
            var database = new Mock<IDatabaseAsync>(MockBehavior.Strict);
            database.Setup(db => db.SetMembersAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, CommandFlags _) => Task.FromResult(Set(key).ToArray()));
            database.Setup(db => db.SetLengthAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, CommandFlags _) => Task.FromResult((long)Set(key).Count));
            database.Setup(db => db.SetAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, RedisValue value, CommandFlags _) => Task.FromResult(Set(key).Add(value)));
            database.Setup(db => db.SetRemoveAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, RedisValue value, CommandFlags _) => Task.FromResult(Set(key).Remove(value)));

            _repository = new RedisInlineBotBlockRepository(() => database.Object);
        }

        private static InlineBotBlock Parse(string input)
        {
            Assert.AreEqual(InlineBotBlockParseResult.Ok, InlineBotBlock.TryParse(input, out var block, out _));
            return block;
        }

        [TestMethod]
        public async Task EmptyChat_HasNoEntries()
        {
            Assert.AreEqual(0, (await _repository.GetAsync(ChatId)).Count);
            Assert.AreEqual(0L, await _repository.CountAsync(ChatId));
        }

        [TestMethod]
        public async Task Entries_RoundTripThroughStorage()
        {
            var gif = Parse("@gif");
            var spam = Parse("/^spam/");
            var gifPattern = Parse("/gif/");

            Assert.IsTrue(await _repository.AddAsync(ChatId, gif));
            Assert.IsTrue(await _repository.AddAsync(ChatId, spam));
            Assert.IsTrue(await _repository.AddAsync(ChatId, gifPattern), "same text, different kind");

            var stored = await _repository.GetAsync(ChatId);
            CollectionAssert.AreEquivalent(new[] { gif, spam, gifPattern }, stored.ToArray());
            Assert.AreEqual(3L, await _repository.CountAsync(ChatId));
        }

        [TestMethod]
        public async Task StorageFormat_IsStable()
        {
            // Existing data depends on this layout; changing it needs a migration.
            await _repository.AddAsync(ChatId, Parse("@gif"));
            await _repository.AddAsync(ChatId, Parse("/^spam/"));

            CollectionAssert.AreEquivalent(new[] { Key }, _sets.Keys.ToArray());
            CollectionAssert.AreEquivalent(new RedisValue[] { "u:gif", "r:^spam" }, _sets[Key].ToArray());
        }

        [TestMethod]
        public async Task AddingTwice_ReportsTheDuplicate()
        {
            Assert.IsTrue(await _repository.AddAsync(ChatId, Parse("@gif")));
            Assert.IsFalse(await _repository.AddAsync(ChatId, Parse("@GIF")));
            Assert.AreEqual(1L, await _repository.CountAsync(ChatId));
        }

        [TestMethod]
        public async Task Remove_ReportsWhetherTheEntryExisted()
        {
            await _repository.AddAsync(ChatId, Parse("@gif"));
            Assert.IsTrue(await _repository.RemoveAsync(ChatId, Parse("@gif")));
            Assert.IsFalse(await _repository.RemoveAsync(ChatId, Parse("@gif")));
            Assert.IsFalse(await _repository.RemoveAsync(ChatId, Parse("/gif/")));
        }

        [TestMethod]
        public async Task Chats_AreIndependent()
        {
            await _repository.AddAsync(ChatId, Parse("@gif"));
            Assert.AreEqual(0, (await _repository.GetAsync(-42)).Count);
        }

        [TestMethod]
        public async Task UnreadableMembers_AreSkipped()
        {
            await _repository.AddAsync(ChatId, Parse("@gif"));
            Set(Key).Add("garbage");
            Set(Key).Add("u:");
            Set(Key).Add("r:");

            var stored = await _repository.GetAsync(ChatId);
            CollectionAssert.AreEqual(new[] { Parse("@gif") }, stored.ToArray());
        }

        [TestMethod]
        public void Constructing_DoesNotTouchTheDatabase()
        {
            // The composition root builds this before Redis.Start() has connected.
            _ = new RedisInlineBotBlockRepository(() => throw new InvalidOperationException("not connected"));
            Assert.IsInstanceOfType(Repositories.InlineBotBlocks, typeof(RedisInlineBotBlockRepository));
        }
    }
}
