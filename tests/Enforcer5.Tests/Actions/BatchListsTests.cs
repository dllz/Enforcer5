using System.Collections.Generic;
using System.Linq;
using System.Text;
using Enforcer5.Actions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Actions
{
    [TestClass]
    public class BatchListsTests
    {
        private static string Lines(int budget, out bool cut, params BatchLists.Section[] sections) =>
            Enforcer5.Helpers.Ids.ToHtml(BatchLists.Lines(sections, budget, (key, ids) => $"{key}: {ids}", left => $"+{left}", out cut));

        [TestMethod]
        public void EachListWithMembers_IsOneLine_InOrder()
        {
            var text = Lines(1000, out var cut,
                new BatchLists.Section("done", new long[] { 111111, 222222 }),
                new BatchLists.Section("refused", new long[0]),
                new BatchLists.Section("blocked", new long[] { 333333 }));
            Assert.AreEqual("\ndone: <code>111111, 222222</code>\nblocked: <code>333333</code>", text, "one list copies with one tap");
            Assert.IsFalse(cut);
        }

        [TestMethod]
        public void WhatDoesNotFit_IsCounted_AndTheFirstListsComeFirst()
        {
            var many = Enumerable.Range(0, 100).Select(i => 1000000L + i).ToArray();
            var text = Lines(50, out var cut,
                new BatchLists.Section("done", many),
                new BatchLists.Section("refused", new long[] { 5555555 }));
            Assert.IsTrue(cut);
            var lines = text.Split('\n').Skip(1).ToArray();
            StringAssert.StartsWith(lines[0], "done: <code>1000000, 1000001, 1000002");
            Assert.IsTrue(lines[0].EndsWith(" +95"), lines[0]);
            Assert.AreEqual("refused: +1", lines[1], "no room left: only how many");
        }

        [TestMethod]
        public void Join_StopsAtTheBudget()
        {
            Assert.AreEqual(("1234567, 7654321", 1), BatchLists.Join(new long[] { 1234567, 7654321, 1111111 }, 20));
            Assert.AreEqual(("", 0), BatchLists.Join(new long[0], 20));
        }

        [TestMethod]
        public void TheCsv_QuotesWhatNeedsIt()
        {
            var csv = Encoding.UTF8.GetString(BatchLists.Csv(new[] { "id", "member" },
                new IReadOnlyList<string>[] { new[] { "1", "Smith, \"J\"" }, new[] { "2", null } }));
            Assert.AreEqual("id,member\n1,\"Smith, \"\"J\"\"\"\n2,\n", csv);
        }
    }
}
