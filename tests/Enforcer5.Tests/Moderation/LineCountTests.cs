using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Moderation
{
    [TestClass]
    public class LineCountTests
    {
        [DataTestMethod]
        [DataRow("one line", 1)]
        [DataRow("one\ntwo", 2)]
        [DataRow("one\r\ntwo\r\nthree", 3)]
        [DataRow("one\ntwo\n", 2)]
        [DataRow("one\n\nthree", 3)]
        [DataRow("", 1)]
        public void CountsLinesAsTheReaderSeesThem(string text, int lines) =>
            Assert.AreEqual(lines, OnMessage.LineCount(text));
    }
}
