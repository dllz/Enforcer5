using Enforcer5.Detection;
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
    }
}
