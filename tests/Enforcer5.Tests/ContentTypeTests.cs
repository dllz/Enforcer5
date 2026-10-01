using System;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StackExchange.Redis;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests
{
    /// <summary>
    /// Methods.GetContentType names the media type that the media block list, the flood
    /// exceptions and the media warnings are keyed by.
    /// </summary>
    [TestClass]
    public class ContentTypeTests
    {
        private static Message Build(string kind)
        {
            switch (kind)
            {
                case "text": return new Message { Text = "hello" };
                case "url":
                    return new Message
                    {
                        Text = "see example.com",
                        Entities = new[] { new MessageEntity { Type = MessageEntityType.Url, Offset = 4, Length = 11 } }
                    };
                case "textlink":
                    return new Message
                    {
                        Text = "click",
                        Entities = new[] { new MessageEntity { Type = MessageEntityType.TextLink, Offset = 0, Length = 5, Url = "https://example.com" } }
                    };
                case "photo": return new Message { Photo = new[] { new PhotoSize() } };
                // What Telegram sends for a GIF today: Animation, plus Document for compatibility.
                case "animation": return new Message { Animation = new Animation(), Document = new Document { MimeType = "video/mp4" } };
                // How 13.x saw a GIF: a bare video/mp4 document.
                case "mp4document": return new Message { Document = new Document { MimeType = "video/mp4" } };
                case "pdf": return new Message { Document = new Document { MimeType = "application/pdf" } };
                case "nomime": return new Message { Document = new Document() };
                case "voice": return new Message { Voice = new Voice() };
                case "sticker": return new Message { Sticker = new Sticker() };
                case "video": return new Message { Video = new Video() };
                case "audio": return new Message { Audio = new Audio() };
                case "contact": return new Message { Contact = new Contact() };
                case "videonote": return new Message { VideoNote = new VideoNote() };
                case "dice": return new Message { Dice = new Dice() };
                default: throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
            }
        }

        [DataTestMethod]
        [DataRow("text", "text")]
        [DataRow("url", "link")]
        [DataRow("textlink", "link")]
        [DataRow("photo", "image")]
        [DataRow("animation", "gif")]
        [DataRow("mp4document", "gif")]
        [DataRow("pdf", "file")]
        [DataRow("nomime", "unknown")]
        [DataRow("voice", "voice")]
        [DataRow("sticker", "sticker")]
        [DataRow("video", "video")]
        [DataRow("audio", "audio")]
        [DataRow("contact", "contact")]
        [DataRow("videonote", "videoNote")]
        [DataRow("dice", "unknown")]
        public void GetContentType_NamesTheMediaSetting(string kind, string expected)
        {
            Assert.AreEqual(expected, Methods.GetContentType(Build(kind)));
        }

        [TestMethod]
        public void Field_ReadsOneFieldFromAFetchedHash()
        {
            var entries = new[] { new HashEntry("gif", "blocked"), new HashEntry("action", "ban") };
            Assert.AreEqual("blocked", (string)MessageContext.Field(entries, "gif"));
            Assert.IsTrue(MessageContext.Field(entries, "sticker").IsNull);
            Assert.IsTrue(MessageContext.Field(null, "gif").IsNull, "a failed read reads as not configured");
        }
    }
}
