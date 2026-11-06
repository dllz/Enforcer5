using System.Linq;
using Enforcer5.Detection;
using Enforcer5.Raids;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.Detection
{
    /// <summary>Detection step 9: what counts as a link outside, and what is compared between newcomers.</summary>
    [TestClass]
    public class MessageSignsTests
    {
        private const long Chat = -1001234567890;

        private static Message WithUrl(string text, string url = null) => new Message
        {
            Text = text,
            Entities = new[]
            {
                url == null
                    ? new MessageEntity { Type = MessageEntityType.Url, Offset = 0, Length = text.Length }
                    : new MessageEntity { Type = MessageEntityType.TextLink, Offset = 0, Length = text.Length, Url = url }
            }
        };

        [DataTestMethod]
        [DataRow("https://example.com", 1)]
        [DataRow("t.me/othergroup", 1)]
        [DataRow("t.me/mygroup", 0)]
        [DataRow("https://t.me/MyGroup/123", 0)]
        [DataRow("https://t.me/c/1234567890/55", 0)]
        [DataRow("https://t.me/c/999/55", 1)]
        [DataRow("www.telegram.me/mygroupfake", 1)]
        public void OutsideLinks(string url, int expected) =>
            Assert.AreEqual(expected, MessageSigns.OutsideLinks(WithUrl(url), "mygroup", Chat));

        [TestMethod]
        public void AHiddenLink_IsALink() =>
            Assert.AreEqual(1, MessageSigns.OutsideLinks(WithUrl("click", "https://scam.example"), "mygroup", Chat));

        [TestMethod]
        public void ShortTexts_AreNotCompared_UnlessWithALink()
        {
            Assert.IsNull(MessageSigns.ContentOf(new Message { Text = "hello everyone" }, false));
            Assert.IsNotNull(MessageSigns.ContentOf(new Message { Text = "join" }, true));
            Assert.IsNotNull(MessageSigns.ContentOf(new Message { Text = "this is a much longer text than a greeting" }, false));
            Assert.AreEqual("u1", MessageSigns.ContentOf(new Message { Sticker = new Sticker { FileUniqueId = "u1" } }, false).Media);
        }
    
        private static Message Command(string text, int length) => new Message
        {
            Text = text,
            Entities = new[] { new MessageEntity { Type = MessageEntityType.BotCommand, Offset = 0, Length = length } }
        };

        [TestMethod]
        public void CommandBot_IsTheBotACommandIsFor()
        {
            Assert.AreEqual("blackwerewolfbot", MessageSigns.CommandBot(Command("/startmighty@BlackWerewolfBot", 29)));
            Assert.AreEqual("quizbot", MessageSigns.CommandBot(Command("/vote@quizbot 3", 13)));
            Assert.IsNull(MessageSigns.CommandBot(Command("/startgame", 10)), "to every bot");
            Assert.IsNull(MessageSigns.CommandBot(new Message { Text = "/startmighty@blackwerewolfbot" }), "not marked a command");
            var later = Command("hey /start@somebot", 14);
            later.Entities[0].Offset = 4;
            Assert.IsNull(MessageSigns.CommandBot(later), "not at the start");
        }

        [TestMethod]
        public void OutsideLinkTargets_AreTheAddresses_HiddenOnesToo_NotThisGroup()
        {
            var message = new Message
            {
                Text = "t.me/mygroup and t.me/other and here",
                Entities = new[]
                {
                    new MessageEntity { Type = MessageEntityType.Url, Offset = 0, Length = 12 },
                    new MessageEntity { Type = MessageEntityType.Url, Offset = 17, Length = 10 },
                    new MessageEntity { Type = MessageEntityType.TextLink, Offset = 32, Length = 4, Url = "https://scam.example" }
                }
            };
            CollectionAssert.AreEqual(new[] { "t.me/other", "https://scam.example" },
                MessageSigns.OutsideLinkTargets(message, "mygroup", Chat).ToArray());
        }

        [TestMethod]
        public void ANameTelegramCannotFind_IsUnknown_UnlessItIsABots()
        {
            Assert.AreEqual(MentionPlace.Unknown, MentionResolver.NotFound("ghostplayer"));
            Assert.AreEqual(MentionPlace.Outside, MentionResolver.NotFound("spamdropsbot"));
            Assert.AreEqual(MentionPlace.Outside, MentionResolver.NotFound("SpamDropsBot"));
        }

        [TestMethod]
        public void FlaggedText_WritesOutHiddenLinksAndMentions()
        {
            var message = new Message
            {
                Text = "click here and ask Bob",
                Entities = new[]
                {
                    new MessageEntity { Type = MessageEntityType.TextLink, Offset = 6, Length = 4, Url = "https://scam.example" },
                    new MessageEntity { Type = MessageEntityType.TextMention, Offset = 19, Length = 3, User = new User { Id = 42 } }
                }
            };
            Assert.AreEqual("click here [https://scam.example] and ask Bob [id 42]", FlaggedText.Of(message));
            Assert.AreEqual("a caption", FlaggedText.Of(new Message { Caption = "a caption" }));
            Assert.IsNull(FlaggedText.Of(new Message { Sticker = new Sticker() }));
            Assert.AreEqual(FlaggedText.MaxLength, FlaggedText.Of(new Message { Text = new string('x', 5000) }).Length);
        }
}
}
