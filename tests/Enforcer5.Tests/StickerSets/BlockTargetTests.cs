using System.Linq;
using System.Xml.Linq;
using Enforcer5.Detection;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.StickerSets
{
    /// <summary>/block's and /unblock's argument and replies, and what /blocked lists (Daniel, 2026-11-09).</summary>
    [TestClass]
    public class BlockTargetTests
    {
        [DataTestMethod]
        [DataRow("@Gif", (int)BlockKind.InlineBot, "@gif")]
        [DataRow("/^spam/", (int)BlockKind.InlineBot, "/^spam/")]
        [DataRow("https://t.me/addemoji/Hearts", (int)BlockKind.EmojiSet, "hearts")]
        [DataRow("t.me/addstickers/Animals", (int)BlockKind.StickerSet, "animals")]
        [DataRow("Animals", (int)BlockKind.Pack, "animals")]
        public void Parse_ReadsEachKind(string input, int kind, string value)
        {
            Assert.AreEqual(BlockParseResult.Ok, BlockTargets.Parse(input, out var target, out _));
            Assert.AreEqual((BlockKind)kind, target.Kind);
            Assert.AreEqual(value, target.Value);
        }

        [DataTestMethod]
        [DataRow("bad-name", (int)BlockParseResult.Invalid)]
        [DataRow("@1x", (int)BlockParseResult.InvalidUsername)]
        [DataRow("/[/", (int)BlockParseResult.InvalidPattern)]
        [DataRow(" ", (int)BlockParseResult.Empty)]
        public void Parse_RefusesWhatIsNone(string input, int result)
        {
            Assert.AreEqual((BlockParseResult)result, BlockTargets.Parse(input, out var target, out _));
            Assert.IsNull(target);
        }

        [TestMethod]
        public void Of_OffersThePacksTheBotAndTheFile_InThatOrder()
        {
            var message = new Message
            {
                Sticker = new Sticker { FileUniqueId = "s1", SetName = "Animals" },
                ViaBot = new User { Id = 5, IsBot = true, Username = "Gif" }
            };
            var targets = BlockTargets.Of(message, new[] { "hearts", null, "hearts", "stars" });
            CollectionAssert.AreEqual(
                new[] { (BlockKind.StickerSet, "animals"), (BlockKind.EmojiSet, "hearts"), (BlockKind.EmojiSet, "stars"), (BlockKind.InlineBot, "@gif"), (BlockKind.File, "s1") },
                targets.Select(t => (t.Kind, t.Value)).ToArray());
            Assert.AreEqual("sticker", targets.Last().MediaType);
            Assert.AreEqual(0, BlockTargets.Of(new Message { Text = "hi" }, null).Count);
        }

        [TestMethod]
        public void IdsOf_ReadsTheCustomEmojiOfTheTextAndCaption_Once()
        {
            var message = new Message
            {
                Entities = new[]
                {
                    new MessageEntity { Type = MessageEntityType.CustomEmoji, CustomEmojiId = "a" },
                    new MessageEntity { Type = MessageEntityType.Bold },
                    new MessageEntity { Type = MessageEntityType.CustomEmoji, CustomEmojiId = "a" }
                },
                CaptionEntities = new[] { new MessageEntity { Type = MessageEntityType.CustomEmoji, CustomEmojiId = "b" } }
            };
            CollectionAssert.AreEqual(new[] { "a", "b" }, CustomEmojis.IdsOf(message).ToArray());
            Assert.AreEqual(0, CustomEmojis.IdsOf(null).Count);
        }

        [TestMethod]
        public void RoundRobin_TakesThePacksInTurn()
        {
            CustomEmojis.Emoji E(string id, string set) => new(id, set, "u" + id, CustomEmojis.Static);
            var order = EmojiCheck.RoundRobin(new[] { E("a1", "a"), E("a2", "a"), E("a3", "a"), E("b1", "b"), E("n1", null) });
            CollectionAssert.AreEqual(new[] { "a1", "b1", "n1", "a2", "a3" }, order.Select(e => e.Id).ToArray());
        }

        [TestMethod]
        public void BlockedList_ShowsEachListAndMarksDetectionsBans()
        {
            var english = XDocument.Load(TestEnvironment.EnglishXmlPath);
            var policy = DetectionPolicy.Empty with
            {
                StickerSets = new[] { "animals" },
                InlineBots = new[] { new InlineBotBlock(InlineBotBlockKind.Username, "gif") },
                EmojiSets = new[]
                {
                    new EmojiSetEntry("hearts", EmojiSetEntry.Admin), new EmojiSetEntry("peaches", EmojiSetEntry.Auto),
                    new EmojiSetEntry("beach", EmojiSetEntry.Allowed)
                }
            };
            var text = Ids.Strip(Commands.BlockedList(english, policy, 3));
            StringAssert.Contains(text, "animals");
            StringAssert.Contains(text, "hearts");
            StringAssert.Contains(text, "peaches (🔞 found NSFW)");
            Assert.IsFalse(text.Contains("beach"), "an allowed pack is not blocked");
            StringAssert.Contains(text, "@gif");
            StringAssert.Contains(text, "Files</b>: 3");
            Assert.AreEqual(Methods.GetLocaleString(english, "blockedNothing"), Commands.BlockedList(english, DetectionPolicy.Empty, 0));
        }
    }
}
