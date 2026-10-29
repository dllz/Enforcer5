using System;
using Enforcer5.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.Detection
{
    /// <summary>Detection step 8: what an edit is, from the record of the post and the times.</summary>
    [TestClass]
    public class EditCheckTests
    {
        private static readonly DateTime Posted = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Since = Posted.AddDays(-1);

        [TestMethod]
        public void SwapsAndLateEdits()
        {
            var swap = EditCheck.Judge("photo:a", "sticker:b", Posted, Posted.AddMinutes(1), Since, "t", "t");
            Assert.AreEqual(("edit_swap", "photo", "sticker"), (swap.Sign, swap.From, swap.To));
            Assert.AreEqual("edit_swap", EditCheck.Judge("photo:a", "photo:b", Posted, Posted.AddMinutes(1), Since, "t", "t").Sign, "another photo");
            Assert.AreEqual(("text", "video"), (EditCheck.Judge(null, "video:v", Posted, Posted.AddMinutes(1), Since, "t", "t").From,
                EditCheck.Judge(null, "video:v", Posted, Posted.AddMinutes(1), Since, "t", "t").To));
            Assert.AreEqual("edit_swap", EditCheck.Judge("photo:a", "sticker:b", Posted, Posted.AddDays(2), Since, null, "t").Sign,
                "a swap needs no text record");
            Assert.IsNull(EditCheck.Judge("photo:a", "photo:a", Posted, Posted.AddMinutes(9), Since, "t", "u"), "a caption fixed in time");
            Assert.IsNull(EditCheck.Judge(null, null, Posted, Posted.AddMinutes(9), Since, "t", "u"), "text fixed in time");
            Assert.AreEqual("edit_late", EditCheck.Judge("photo:a", "photo:a", Posted, Posted.AddMinutes(11), Since, "t", "u").Sign);
            Assert.AreEqual("edit_late", EditCheck.Judge(null, null, Posted, Posted.AddHours(40), Since, "t", "u").Sign);
            Assert.IsNull(EditCheck.Judge(null, "photo:x", Since.AddHours(-1), Since.AddMinutes(-55), Since, null, "t"), "before recording, in time");
        }

        [TestMethod]
        public void ALateEdit_NeedsAChangeWeCanSee()
        {
            Assert.IsNull(EditCheck.Judge(null, null, Posted, Posted.AddHours(7), Since, "t", "t"),
                "nothing changed: Telegram re-sends a member's posts when their tag is set");
            Assert.IsNull(EditCheck.Judge("photo:a", "photo:a", Posted, Posted.AddHours(7), Since, "t", "t"), "the same photo and caption");
            Assert.IsNull(EditCheck.Judge(null, null, Posted, Posted.AddMinutes(11), null, null, "t"), "not recorded: can't tell");
            Assert.IsNull(EditCheck.Judge(null, "photo:x", Posted, Posted.AddDays(40), Since, null, "t"), "past the record window");
        }

        [TestMethod]
        public void TheTextHash_SeesTextCaptionsAndHiddenLinks_NotFormatting()
        {
            var plain = new Message { Text = "hello there" };
            var bold = new Message { Text = "hello there", Entities = new[] { new MessageEntity { Type = MessageEntityType.Bold, Offset = 0, Length = 5 } } };
            var link = new Message { Text = "hello there", Entities = new[] { new MessageEntity { Type = MessageEntityType.TextLink, Offset = 0, Length = 5, Url = "https://a.example" } } };
            var other = new Message { Text = "hello there", Entities = new[] { new MessageEntity { Type = MessageEntityType.TextLink, Offset = 0, Length = 5, Url = "https://b.example" } } };
            var caption = new Message { Caption = "hello there" };

            Assert.AreEqual(EditCheck.TextHash(plain), EditCheck.TextHash(bold));
            Assert.AreEqual(EditCheck.TextHash(plain), EditCheck.TextHash(caption));
            Assert.AreNotEqual(EditCheck.TextHash(plain), EditCheck.TextHash(link));
            Assert.AreNotEqual(EditCheck.TextHash(link), EditCheck.TextHash(other), "a link swapped under the same words");
            Assert.AreNotEqual(EditCheck.TextHash(plain), EditCheck.TextHash(new Message { Text = "hello there!" }));
            Assert.AreEqual(EditCheck.TextHash(new Message()), EditCheck.TextHash(new Message { Caption = "" }), "no text at all");
        }
    }
}
