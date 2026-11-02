using System;
using System.Linq;
using Enforcer5.Actions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Actions
{
    [TestClass]
    public class BatchPlanTests
    {
        private static readonly TimeSpan Default = TimeSpan.FromHours(1);

        [TestMethod]
        public void TheIds_AreEveryUserIdInTheText_InOrder()
        {
            var text = "123456789\n987654321, 123456789\nid: 55555 x\n@name 12345678901 t.me/c/1234567/89 -1001234567890 12a345678";
            CollectionAssert.AreEqual(new long[] { 123456789, 987654321, 123456789, 55555, 12345678901 }, BatchPlan.Ids(text).ToArray());
            Assert.AreEqual(0, BatchPlan.Ids(null).Count);
            Assert.AreEqual(0, BatchPlan.Ids("1234").Count, "too short for a user id");
        }

        [DataTestMethod]
        [DataRow("ban spam ring", "ban", "spam ring", null)]
        [DataRow("BAN", "ban", null, null)]
        [DataRow("tempban 2d raid", "tempban", "raid", 2880.0)]
        [DataRow("tempban 2 hours raid wave", "tempban", "raid wave", 120.0)]
        [DataRow("tempmute", "tempmute", null, 60.0)]
        [DataRow("tempmute raid", "tempmute", "raid", 60.0)]
        [DataRow("/resetwarns appeal", "resetwarns", "appeal", null)]
        public void AnOrder_IsReadOnce(string text, string trigger, string reason, double? minutes)
        {
            Assert.AreEqual(BatchPlan.ReadResult.Ok, BatchPlan.Read(text, Default, out var order));
            Assert.AreEqual((trigger, reason, minutes), (order.Command.Trigger, order.Reason, order.Duration?.TotalMinutes));
        }

        [TestMethod]
        public void NoCommand_OrABadDuration_IsSaid()
        {
            Assert.AreEqual(BatchPlan.ReadResult.UnknownCommand, BatchPlan.Read("", Default, out _));
            Assert.AreEqual(BatchPlan.ReadResult.UnknownCommand, BatchPlan.Read("delete everything", Default, out _));
            Assert.AreEqual(BatchPlan.ReadResult.InvalidDuration, BatchPlan.Read("tempban 2x", Default, out _));
        }

        [TestMethod]
        public void EveryCommand_IsAnActionTheSeamCarriesOut()
        {
            CollectionAssert.AreEquivalent(
                new[] { "ban", "tempban", "unban", "kick", "mute", "tempmute", "unmute", "warn", "prewarn", "removewarn", "resetwarns" },
                BatchPlan.Commands.Select(c => c.Trigger).ToArray(), "as the batches_kind_check constraint allows");
            Assert.IsTrue(BatchPlan.Commands.All(c => c.Kind != ActionKind.Delete && c.Kind != ActionKind.MediaStrike));
        }
    }
}
