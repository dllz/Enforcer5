using System.Linq;
using System.Xml.Linq;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.InlineBots
{
    [TestClass]
    public class InlineBotTestTests
    {
        private static XDocument _english;

        [ClassInitialize]
        public static void Load(TestContext context)
        {
            _english = XDocument.Load(TestEnvironment.EnglishXmlPath);
        }

        private static InlineBotTest Parse(string input, string repliedBot = null)
        {
            var result = InlineBotTest.TryParse(input, repliedBot, out var test, out var detail);
            Assert.AreEqual(InlineBotTestParseResult.Ok, result, $"{input}: {detail}");
            return test;
        }

        private static InlineBotBlock Block(string input)
        {
            Assert.AreEqual(InlineBotBlockParseResult.Ok, InlineBotBlock.TryParse(input, out var block, out _));
            return block;
        }

        [TestMethod]
        public void Usernames_AreTestedAgainstTheBlocklist()
        {
            var test = Parse("@SpamBot gif");
            Assert.IsNull(test.Pattern);
            CollectionAssert.AreEqual(new[] { "spambot", "gif" }, test.Usernames.ToArray());
        }

        [TestMethod]
        public void DuplicateUsernames_AreTestedOnce()
        {
            CollectionAssert.AreEqual(new[] { "gif" }, Parse("@gif @GIF gif").Usernames.ToArray());
        }

        [TestMethod]
        public void LeadingPattern_IsTestedInsteadOfTheBlocklist()
        {
            var test = Parse("/^spam/ @spambot @gif");
            Assert.AreEqual(Block("/^spam/"), test.Pattern);
            CollectionAssert.AreEqual(new[] { "spambot", "gif" }, test.Usernames.ToArray());
        }

        [TestMethod]
        public void Pattern_RunsToTheLastSlash_SoItMayContainSpaces()
        {
            var test = Parse("/spam| bot$/ @x_bot");
            Assert.AreEqual("spam| bot$", test.Pattern.Value);
            CollectionAssert.AreEqual(new[] { "x_bot" }, test.Usernames.ToArray());
        }

        [TestMethod]
        public void RepliedBot_IsUsedWhenNoUsernameIsGiven()
        {
            CollectionAssert.AreEqual(new[] { "gifbot" }, Parse(null, "GifBot").Usernames.ToArray());
            var test = Parse("/gif/", "GifBot");
            Assert.IsNotNull(test.Pattern);
            CollectionAssert.AreEqual(new[] { "gifbot" }, test.Usernames.ToArray());
        }

        [TestMethod]
        public void GivenUsernames_WinOverTheRepliedBot()
        {
            CollectionAssert.AreEqual(new[] { "vid" }, Parse("@vid", "GifBot").Usernames.ToArray());
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        [DataRow("/^spam/")]   // a pattern but nothing to test it against
        public void NothingToTest_IsEmpty(string input)
        {
            Assert.AreEqual(InlineBotTestParseResult.Empty, InlineBotTest.TryParse(input, null, out _, out _));
        }

        [DataTestMethod]
        [DataRow("@ab", "@ab")]
        [DataRow("@gif @1x", "@1x")]
        [DataRow("/abc", "/abc")]          // no closing slash: not a pattern
        [DataRow("@gif /^spam/", "/^spam/")] // the pattern must come first
        public void InvalidUsername_IsNamed(string input, string expected)
        {
            Assert.AreEqual(InlineBotTestParseResult.InvalidUsername, InlineBotTest.TryParse(input, null, out _, out var detail));
            Assert.AreEqual(expected, detail);
        }

        [TestMethod]
        public void UsernameCount_IsCapped()
        {
            string Names(int n) => string.Join(" ", Enumerable.Range(0, n).Select(i => $"@bot{i:D2}"));
            Assert.AreEqual(InlineBotTestParseResult.Ok,
                InlineBotTest.TryParse(Names(InlineBotTest.MaxUsernames), null, out _, out _));
            Assert.AreEqual(InlineBotTestParseResult.TooManyUsernames,
                InlineBotTest.TryParse(Names(InlineBotTest.MaxUsernames + 1), null, out _, out _));
        }

        [DataTestMethod]
        [DataRow("/[/ @gif")]
        [DataRow("/(?=a)b/ @gif")]
        [DataRow("// @gif")]
        public void UnusablePattern_IsRejectedWithAReason(string input)
        {
            Assert.AreEqual(InlineBotTestParseResult.InvalidPattern, InlineBotTest.TryParse(input, null, out _, out var detail));
            Assert.IsFalse(string.IsNullOrWhiteSpace(detail));
        }

        [TestMethod]
        public void OverlongPattern_IsRejected()
        {
            var input = "/" + new string('a', InlineBotPattern.MaxLength + 1) + "/ @gif";
            Assert.AreEqual(InlineBotTestParseResult.PatternTooLong, InlineBotTest.TryParse(input, null, out _, out _));
        }

        [TestMethod]
        public void AllMatches_ListsEveryMatchingEntryInOrder()
        {
            var broken = new InlineBotBlock(InlineBotBlockKind.Pattern, "(");
            var blocks = new[] { Block("/^spam/"), Block("@gif"), broken, Block("/bot$/") };
            CollectionAssert.AreEqual(new[] { Block("/^spam/"), Block("/bot$/") },
                InlineBotBlock.AllMatches(blocks, "spambot").ToArray(), "the broken entry is skipped");
            Assert.AreEqual(0, InlineBotBlock.AllMatches(blocks, "vid").Count);
            Assert.AreEqual(0, InlineBotBlock.AllMatches(null, "vid").Count);
        }

        [TestMethod]
        public void Reply_ForAPattern_SaysWhichBotsItMatches()
        {
            var reply = Commands.InlineBotTestReply(_english, Parse("/^spam/ @spambot @gif"), null);
            var lines = reply.Split('\n');
            Assert.AreEqual(3, lines.Length, reply);
            StringAssert.Contains(lines[0], "/^spam/");
            Assert.AreEqual(Methods.GetLocaleString(_english, "inlineTestMatch", "@spambot"), lines[1]);
            Assert.AreEqual(Methods.GetLocaleString(_english, "inlineTestNoMatch", "@gif"), lines[2]);
        }

        [TestMethod]
        public void Reply_ForTheBlocklist_NamesEveryMatchingEntry()
        {
            var blocks = new[] { Block("@gif"), Block("/^spam/"), Block("/bot$/") };
            var reply = Commands.InlineBotTestReply(_english, Parse("@spambot @vid"), blocks);
            var lines = reply.Split('\n');
            Assert.AreEqual(Methods.GetLocaleString(_english, "inlineTestBlockedBy", "@spambot", "/^spam/, /bot$/"), lines[0]);
            Assert.AreEqual(Methods.GetLocaleString(_english, "inlineTestNotBlocked", "@vid"), lines[1]);
        }

        [TestMethod]
        public void Reply_WithAnEmptyBlocklist_SaysSo()
        {
            Assert.AreEqual(Methods.GetLocaleString(_english, "inlineBlockListEmpty"),
                Commands.InlineBotTestReply(_english, Parse("@gif"), new InlineBotBlock[0]));
        }

        [TestMethod]
        public void Reply_EscapesThePatternForHtml()
        {
            // Replies are sent as HTML; an unescaped < would make Telegram reject the message.
            var reply = Commands.InlineBotTestReply(_english, Parse("/a<b/ @gif"), null);
            StringAssert.Contains(reply, "/a&lt;b/");
            Assert.IsFalse(reply.Contains("a<b"), reply);
        }
    }
}
