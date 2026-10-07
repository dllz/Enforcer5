using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using StackExchange.Redis;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.StickerSets
{
    [TestClass]
    public class StickerSetNameTests
    {
        [DataTestMethod]
        [DataRow("Animals", "animals")]
        [DataRow("  Some_Pack_by_StickersBot  ", "some_pack_by_stickersbot")]
        [DataRow("https://t.me/addstickers/Animals", "animals")]
        [DataRow("http://t.me/addstickers/Animals/", "animals")]
        [DataRow("t.me/addstickers/Animals", "animals")]
        [DataRow("https://telegram.me/addstickers/Animals", "animals")]
        [DataRow("HTTPS://T.ME/AddStickers/Animals", "animals")]
        [DataRow("tg://addstickers?set=Animals", "animals")]
        [DataRow("https://t.me/addemoji/Hearts", "hearts")] // accepted so /blocksticker can explain
        [DataRow("123", "123")]
        public void NamesAndLinks_AreNormalised(string input, string expected)
        {
            Assert.AreEqual(expected, StickerSetName.Parse(input));
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow("two words")]
        [DataRow("bad-name")]
        [DataRow("https://t.me/somechannel")]
        [DataRow("https://t.me/addstickers/")]
        [DataRow("https://t.me/addstickers/a/b")]
        [DataRow("https://example.com/addstickers/Animals")]
        public void Everything_Else_IsRejected(string input)
        {
            Assert.IsNull(StickerSetName.Parse(input));
        }

        [TestMethod]
        public void Length_IsCappedAtTelegramsLimit()
        {
            Assert.IsNotNull(StickerSetName.Parse(new string('a', StickerSetName.MaxLength)));
            Assert.IsNull(StickerSetName.Parse(new string('a', StickerSetName.MaxLength + 1)));
        }

        [TestMethod]
        public void StickerWithoutAPack_HasNoName()
        {
            Assert.IsNull(StickerSetName.Normalise(null));
            Assert.AreEqual("animals", StickerSetName.Normalise("Animals"));
        }

        [TestMethod]
        public void Link_PointsAtTheRightKindOfPack()
        {
            Assert.AreEqual("https://t.me/addstickers/Animals", StickerSetName.Link("Animals"));
            Assert.AreEqual("https://t.me/addemoji/Hearts", StickerSetName.Link("Hearts", customEmoji: true));
            Assert.AreEqual("animals", StickerSetName.Parse(StickerSetName.Link("Animals")), "the link parses back");
        }
    }

    [TestClass]
    public class RedisStickerSetBlockRepositoryTests
    {
        private const long ChatId = -1001234567890;
        private const string Key = "chat:-1001234567890:blockedstickersets";

        private readonly Dictionary<string, HashSet<RedisValue>> _sets = new Dictionary<string, HashSet<RedisValue>>();
        private IStickerSetBlockRepository _repository;

        private HashSet<RedisValue> Set(RedisKey key)
        {
            if (!_sets.TryGetValue(key.ToString(), out var set)) _sets[key.ToString()] = set = new HashSet<RedisValue>();
            return set;
        }

        [TestInitialize]
        public void SetUp()
        {
            var database = new Mock<IDatabaseAsync>(MockBehavior.Strict);
            database.Setup(db => db.SetMembersAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, CommandFlags _) => Task.FromResult(Set(key).ToArray()));
            database.Setup(db => db.SetLengthAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, CommandFlags _) => Task.FromResult((long)Set(key).Count));
            database.Setup(db => db.SetContainsAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, RedisValue value, CommandFlags _) => Task.FromResult(Set(key).Contains(value)));
            database.Setup(db => db.SetAddAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, RedisValue value, CommandFlags _) => Task.FromResult(Set(key).Add(value)));
            database.Setup(db => db.SetRemoveAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, RedisValue value, CommandFlags _) => Task.FromResult(Set(key).Remove(value)));

            _repository = new RedisStickerSetBlockRepository(() => database.Object);
        }

        [TestMethod]
        public async Task EmptyChat_HasNoEntries()
        {
            Assert.AreEqual(0, (await _repository.GetAsync(ChatId)).Count);
            Assert.AreEqual(0L, await _repository.CountAsync(ChatId));
            Assert.IsFalse(await _repository.ContainsAsync(ChatId, "animals"));
        }

        [TestMethod]
        public async Task Entries_RoundTripThroughStorage()
        {
            Assert.IsTrue(await _repository.AddAsync(ChatId, "animals"));
            Assert.IsTrue(await _repository.AddAsync(ChatId, "hearts_by_bot"));
            Assert.IsFalse(await _repository.AddAsync(ChatId, "animals"), "duplicate");

            CollectionAssert.AreEquivalent(new[] { "animals", "hearts_by_bot" }, (await _repository.GetAsync(ChatId)).ToArray());
            Assert.AreEqual(2L, await _repository.CountAsync(ChatId));
            Assert.IsTrue(await _repository.ContainsAsync(ChatId, "animals"));
            Assert.IsFalse(await _repository.ContainsAsync(ChatId, "cats"));
        }

        [TestMethod]
        public async Task StorageFormat_IsStable()
        {
            // Existing data depends on this layout; changing it needs a migration.
            await _repository.AddAsync(ChatId, "animals");
            CollectionAssert.AreEquivalent(new[] { Key }, _sets.Keys.ToArray());
            CollectionAssert.AreEquivalent(new RedisValue[] { "animals" }, _sets[Key].ToArray());
        }

        [TestMethod]
        public async Task Remove_ReportsWhetherTheEntryExisted()
        {
            await _repository.AddAsync(ChatId, "animals");
            Assert.IsTrue(await _repository.RemoveAsync(ChatId, "animals"));
            Assert.IsFalse(await _repository.RemoveAsync(ChatId, "animals"));
            Assert.IsFalse(await _repository.ContainsAsync(ChatId, "animals"));
        }

        [TestMethod]
        public async Task Chats_AreIndependent()
        {
            await _repository.AddAsync(ChatId, "animals");
            Assert.IsFalse(await _repository.ContainsAsync(-42, "animals"));
        }

        [TestMethod]
        public void EmptyName_IsRefused()
        {
            Assert.ThrowsException<ArgumentException>(() => _repository.AddAsync(ChatId, ""));
        }

        [TestMethod]
        public void Constructing_DoesNotTouchTheDatabase()
        {
            _ = new RedisStickerSetBlockRepository(() => throw new InvalidOperationException("not connected"));
            Assert.IsInstanceOfType(Repositories.StickerSetBlocks, typeof(RedisStickerSetBlockRepository));
        }
    }

    /// <summary>
    /// The decision half of OnMessage.BlockedStickerSet; as for inline bots, only the paths that
    /// dispatch nothing are observable here.
    /// </summary>
    [TestClass]
    public class BlockedStickerSetTests
    {
        private const long ChatId = -1001234567890;

        private static MessageContext Context(bool blocked)
        {
            var message = new Message
            {
                Id = 7,
                Chat = new Chat { Id = ChatId, Type = ChatType.Supergroup },
                From = new User { Id = 42, FirstName = "Member" },
                Sticker = new Sticker { SetName = "Animals" }
            };
            return new MessageContext
            {
                Update = new Update { Message = message },
                Message = message,
                ChatId = ChatId,
                UserId = 42,
                IsGroup = true,
                Complete = true,
                StickerSetBlocked = blocked
            };
        }

        [TestMethod]
        public void UnblockedPack_IsLeftAlone()
        {
            Assert.IsFalse(OnMessage.BlockedStickerSet(Context(false)));
        }

        [TestMethod]
        public void WatchedUser_IsExempt()
        {
            var ctx = Context(true);
            ctx.Watched = true;
            Assert.IsFalse(OnMessage.BlockedStickerSet(ctx));
        }

        [TestMethod]
        public void AutomaticForwardFromLinkedChannel_IsExempt()
        {
            var ctx = Context(true);
            ctx.Message.IsAutomaticForward = true;
            Assert.IsFalse(OnMessage.BlockedStickerSet(ctx));
        }

        [TestMethod]
        public void AnonymousAdmin_IsKeptButSkipsTheOtherFilters()
        {
            var ctx = Context(true);
            ctx.Message.From = new User { Id = 1087968824, IsBot = true, FirstName = "Group" };
            ctx.Message.SenderChat = new Chat { Id = ChatId, Type = ChatType.Supergroup };
            Assert.IsTrue(OnMessage.BlockedStickerSet(ctx));
        }
    }

    [TestClass]
    public class StickerInfoReplyTests
    {
        private static XDocument _english;

        [ClassInitialize]
        public static void Load(TestContext context)
        {
            _english = XDocument.Load(TestEnvironment.EnglishXmlPath);
        }

        private static StickerSet Pack(StickerType type = StickerType.Regular, string title = "Cute Animals") =>
            new StickerSet { Name = "Animals", Title = title, StickerType = type, Stickers = new Sticker[3] };

        [TestMethod]
        public void PrivateChat_ShowsThePackAndHowToBlockIt()
        {
            var reply = StickerInfo(Pack(), null);
            StringAssert.Contains(reply, "<b>Cute Animals</b>");
            StringAssert.Contains(reply, "<code>Animals</code>");
            StringAssert.Contains(reply, "https://t.me/addstickers/Animals");
            StringAssert.Contains(reply, "3");
            StringAssert.Contains(reply, "<code>/blocksticker animals</code>");
        }

        [TestMethod]
        public void Group_SaysWhetherThePackIsBlocked()
        {
            StringAssert.Contains(StickerInfo(Pack(), true), "<code>/unblocksticker animals</code>");
            StringAssert.Contains(StickerInfo(Pack(), false), "<code>/blocksticker animals</code>");
        }

        [TestMethod]
        public void EmojiPack_IsExplainedInsteadOfOfferedForBlocking()
        {
            var reply = StickerInfo(Pack(StickerType.CustomEmoji), false);
            StringAssert.Contains(reply, "https://t.me/addemoji/Animals");
            StringAssert.Contains(reply, Methods.GetLocaleString(_english, "stickerInfoEmojiPack"));
            Assert.IsFalse(reply.Contains("/blocksticker"), reply);
        }

        [TestMethod]
        public void FailedLookup_StillShowsTheName()
        {
            var reply = StickerInfo(null, null);
            StringAssert.Contains(reply, "<code>animals</code>");
            StringAssert.Contains(reply, "<code>/blocksticker animals</code>");
        }

        [TestMethod]
        public void Title_IsEscapedForHtml()
        {
            // Pack titles are user-chosen; an unescaped < would make Telegram reject the reply.
            var reply = StickerInfo(Pack(title: "<i>Cats</i> & Dogs"), null);
            StringAssert.Contains(reply, "&lt;i&gt;Cats&lt;/i&gt; &amp; Dogs");
        }

        private static string StickerInfo(StickerSet set, bool? blocked) =>
            Commands.StickerInfoReply(_english, "animals", set, blocked);
    }
}
