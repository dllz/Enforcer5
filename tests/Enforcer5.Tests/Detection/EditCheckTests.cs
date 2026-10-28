using System;
using Enforcer5.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

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
            var swap = EditCheck.Judge("photo:a", "sticker:b", Posted, Posted.AddMinutes(1), Since);
            Assert.AreEqual(("edit_swap", "photo", "sticker"), (swap.Sign, swap.From, swap.To));
            Assert.AreEqual("edit_swap", EditCheck.Judge("photo:a", "photo:b", Posted, Posted.AddMinutes(1), Since).Sign, "another photo");
            Assert.AreEqual(("text", "video"), (EditCheck.Judge(null, "video:v", Posted, Posted.AddMinutes(1), Since).From,
                EditCheck.Judge(null, "video:v", Posted, Posted.AddMinutes(1), Since).To));
            Assert.IsNull(EditCheck.Judge("photo:a", "photo:a", Posted, Posted.AddMinutes(9), Since), "a caption fixed in time");
            Assert.IsNull(EditCheck.Judge(null, null, Posted, Posted.AddMinutes(9), Since), "text fixed in time");
            Assert.AreEqual("edit_late", EditCheck.Judge("photo:a", "photo:a", Posted, Posted.AddMinutes(11), Since).Sign);
            Assert.AreEqual("edit_late", EditCheck.Judge(null, "photo:x", Posted, Posted.AddMinutes(11), null).Sign, "not recorded then: can't tell");
            Assert.AreEqual("edit_late", EditCheck.Judge(null, "photo:x", Posted, Posted.AddDays(40), Since).Sign, "past the record window");
            Assert.IsNull(EditCheck.Judge(null, "photo:x", Since.AddHours(-1), Since.AddMinutes(-55), Since), "before recording, in time");
        }
    }
}
