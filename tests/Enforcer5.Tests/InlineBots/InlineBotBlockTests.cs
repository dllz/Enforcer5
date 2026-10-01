using System;
using System.Diagnostics;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.InlineBots
{
    [TestClass]
    public class InlineBotBlockTests
    {
        private static InlineBotBlock Parse(string input)
        {
            var result = InlineBotBlock.TryParse(input, out var block, out var detail);
            Assert.AreEqual(InlineBotBlockParseResult.Ok, result, $"{input}: {detail}");
            return block;
        }

        private static InlineBotBlockParseResult ParseResult(string input) =>
            InlineBotBlock.TryParse(input, out _, out _);

        [DataTestMethod]
        [DataRow("@gif", "gif")]
        [DataRow("gif", "gif")]
        [DataRow("GIF", "gif")]
        [DataRow("  @Some_Bot  ", "some_bot")]
        [DataRow("@vid", "vid")]
        public void Username_IsNormalisedToLowercaseWithoutAt(string input, string expected)
        {
            var block = Parse(input);
            Assert.AreEqual(InlineBotBlockKind.Username, block.Kind);
            Assert.AreEqual(expected, block.Value);
        }

        [TestMethod]
        public void Username_AcceptsTheTelegramLengthLimits()
        {
            Assert.AreEqual(InlineBotBlockParseResult.Ok, ParseResult("@abc"));
            Assert.AreEqual(InlineBotBlockParseResult.Ok, ParseResult("@" + new string('a', 32)));
        }

        [DataTestMethod]
        [DataRow("@ab")]            // too short
        [DataRow("@1abc")]          // must start with a letter
        [DataRow("@a-bc")]          // invalid character
        [DataRow("@gif please")]    // trailing text
        [DataRow("/")]              // a lone slash is not a pattern
        public void Username_RejectsInvalidInput(string input)
        {
            Assert.AreEqual(InlineBotBlockParseResult.InvalidUsername, ParseResult(input));
        }

        [TestMethod]
        public void Username_RejectsOverlongInput()
        {
            Assert.AreEqual(InlineBotBlockParseResult.InvalidUsername, ParseResult("@" + new string('a', 33)));
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        public void EmptyInput_IsReportedAsEmpty(string input)
        {
            Assert.AreEqual(InlineBotBlockParseResult.Empty, ParseResult(input));
        }

        [TestMethod]
        public void SlashDelimitedInput_IsAPattern()
        {
            var block = Parse("/^spam/");
            Assert.AreEqual(InlineBotBlockKind.Pattern, block.Kind);
            Assert.AreEqual("^spam", block.Value);
        }

        [TestMethod]
        public void EmptyPattern_IsRejected()
        {
            // It would match every bot; /.*/ says that explicitly.
            Assert.AreEqual(InlineBotBlockParseResult.InvalidPattern, ParseResult("//"));
        }

        [TestMethod]
        public void PatternLength_IsCapped()
        {
            Assert.AreEqual(InlineBotBlockParseResult.Ok,
                ParseResult("/" + new string('a', InlineBotPattern.MaxLength) + "/"));
            Assert.AreEqual(InlineBotBlockParseResult.PatternTooLong,
                ParseResult("/" + new string('a', InlineBotPattern.MaxLength + 1) + "/"));
        }

        [DataTestMethod]
        [DataRow("/[/")]            // does not parse
        [DataRow("/(a)\\1/")]       // backreference: unsupported by NonBacktracking
        [DataRow("/(?=a)b/")]       // lookahead
        [DataRow("/(?<=a)b/")]      // lookbehind
        [DataRow("/(a{100}){100}/")] // automaton over the NonBacktracking size limit
        public void UnusablePattern_IsRejectedWithAReason(string input)
        {
            var result = InlineBotBlock.TryParse(input, out var block, out var detail);
            Assert.AreEqual(InlineBotBlockParseResult.InvalidPattern, result);
            Assert.IsNull(block);
            Assert.IsFalse(string.IsNullOrWhiteSpace(detail), "the admin is told why");
        }

        [TestMethod]
        public void HugeCountedLoop_IsRejectedQuickly()
        {
            var timer = Stopwatch.StartNew();
            Assert.AreEqual(InlineBotBlockParseResult.InvalidPattern, ParseResult("/((a{1000}){1000}){1000}/"));
            Assert.IsTrue(timer.ElapsedMilliseconds < 2000, $"took {timer.ElapsedMilliseconds}ms");
        }

        [DataTestMethod]
        [DataRow("@gif")]
        [DataRow("/^spam/")]
        [DataRow("/gif/")]
        public void DisplayForm_ParsesBackToTheSameEntry(string input)
        {
            // /blockedinline output must be pasteable into /unblockinline.
            var block = Parse(input);
            Assert.AreEqual(input, block.ToString());
            Assert.AreEqual(block, Parse(block.ToString()));
        }

        [TestMethod]
        public void UsernameAndPatternWithTheSameText_AreDifferentEntries()
        {
            Assert.AreNotEqual(Parse("@gif"), Parse("/gif/"));
        }

        [TestMethod]
        public void Username_MatchesExactlyIgnoringCase()
        {
            var gif = Parse("@gif");
            Assert.IsTrue(gif.Matches("gif"));
            Assert.IsTrue(gif.Matches("GIF"));
            Assert.IsFalse(gif.Matches("gifbot"));
            Assert.IsFalse(gif.Matches("agif"));
        }

        [TestMethod]
        public void MissingUsername_NeverMatches()
        {
            Assert.IsFalse(Parse("@gif").Matches(null));
            Assert.IsFalse(Parse("/.*/").Matches(""));
        }

        [TestMethod]
        public void Pattern_MatchesAnywhereUnlessAnchored()
        {
            Assert.IsTrue(Parse("/spam/").Matches("nospam_bot"));
            Assert.IsTrue(Parse("/^spam/").Matches("SpamBot"));
            Assert.IsFalse(Parse("/^spam/").Matches("nospambot"));
            Assert.IsTrue(Parse("/^gif$/").Matches("Gif"));
            Assert.IsFalse(Parse("/^gif$/").Matches("gifs"));
            Assert.IsTrue(Parse("/.*/").Matches("vid"));
        }

        [TestMethod]
        public void BacktrackingPattern_StaysLinear()
        {
            // (a+)+$ takes exponential time under a backtracking engine on input like this.
            var evil = Parse("/(a+)+$/");
            var input = new string('a', 31) + "!";
            var timer = Stopwatch.StartNew();
            for (var i = 0; i < 1000; i++) evil.Matches(input);
            Assert.IsTrue(timer.ElapsedMilliseconds < 1000, $"took {timer.ElapsedMilliseconds}ms");
        }

        [TestMethod]
        public void FirstMatch_ReturnsTheMatchingEntry()
        {
            var gif = Parse("@gif");
            var spam = Parse("/^spam/");
            Assert.AreEqual(spam, InlineBotBlock.FirstMatch(new[] { gif, spam }, "spambot"));
            Assert.IsNull(InlineBotBlock.FirstMatch(new[] { gif, spam }, "vid"));
        }

        [TestMethod]
        public void FirstMatch_HandlesMissingInput()
        {
            Assert.IsNull(InlineBotBlock.FirstMatch(null, "gif"));
            Assert.IsNull(InlineBotBlock.FirstMatch(new[] { Parse("/.*/") }, null));
        }

        [TestMethod]
        public void FirstMatch_SkipsAStoredPatternThatCannotBeEvaluated()
        {
            // One bad entry must not switch off the rest of the list.
            var broken = new InlineBotBlock(InlineBotBlockKind.Pattern, "(");
            var gif = Parse("@gif");
            Assert.AreEqual(gif, InlineBotBlock.FirstMatch(new[] { broken, gif }, "gif"));
        }
    }
}
