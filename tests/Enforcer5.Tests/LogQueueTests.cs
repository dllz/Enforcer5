using System.Linq;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Logging
{
    /// <summary>How the log queue packs waiting entries into posts.</summary>
    [TestClass]
    public class LogQueueTests
    {
        private static LogEntry Entry(int n, string text, string link = null) =>
            new LogEntry(n.ToString(), n, -100, -200, text, link, link == null ? null : "Go to message");

        [TestMethod]
        public void OneEntry_IsPostedAsAlways_WithItsButton()
        {
            var (text, buttons) = LogQueue.Compose(new[] { Entry(1, "<b>Beck</b> banned X", "https://t.me/c/1/2") });
            Assert.AreEqual("<b>Beck</b> banned X", text);
            Assert.AreEqual("https://t.me/c/1/2", buttons.InlineKeyboard.Single().Single().Url);
        }

        [TestMethod]
        public void SeveralEntries_AreMerged_InOrder_WithTheirLinksInTheText()
        {
            var (text, buttons) = LogQueue.Compose(new[] { Entry(1, "first", "https://t.me/c/1/2?a&b"), Entry(2, "second") });
            Assert.IsNull(buttons);
            Assert.AreEqual("first\n<a href=\"https://t.me/c/1/2?a&amp;b\">Go to message</a>\n\nsecond", text);
        }

        [TestMethod]
        public void ABatch_StopsAtTheLengthAndCount_ButAlwaysTakesTheOldest()
        {
            var big = new string('x', LogQueue.MaxLength + 100);
            Assert.AreEqual(1, LogQueue.NextBatch(new[] { Entry(1, big), Entry(2, "small") }).Count, "too long alone: sent alone");
            var many = Enumerable.Range(1, 120).Select(i => Entry(i, "line")).ToList();
            var batch = LogQueue.NextBatch(many);
            Assert.AreEqual(LogQueue.MaxEntriesPerPost, batch.Count);
            Assert.AreEqual("1", batch[0].Id);
            var long1 = new string('a', 2000);
            Assert.AreEqual(1, LogQueue.NextBatch(new[] { Entry(1, long1), Entry(2, long1) }).Count, "two would pass the length");
        }
    }
}
