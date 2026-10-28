using System;
using System.Collections.Generic;
using System.Linq;
using Enforcer5.Data;
using Enforcer5.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Detection
{
    /// <summary>Detection step 7b: the arithmetic of learning the established and report levels.</summary>
    [TestClass]
    public class TrustLearningTests
    {
        private static readonly (int, int) Usual = (20, 7);

        [TestMethod]
        public void Raised_GoesHalfwayToTheMember_AtMostThreeTimesTheUsual()
        {
            Assert.AreEqual((23, 7), TrustLearning.Raised((20, 7), Usual, 25, null, out var takeover), "days unknown: left");
            Assert.IsFalse(takeover);
            Assert.AreEqual((23, 10), TrustLearning.Raised((20, 7), Usual, 25, 12, out _));
            Assert.AreEqual((21, 8), TrustLearning.Raised((20, 7), Usual, 20, 7, out _), "always at least one more message");
            Assert.AreEqual((50, 14), TrustLearning.Raised((40, 12), Usual, 59, 300, out _), "days up to twice the usual");
            Assert.IsNull(TrustLearning.Raised((60, 14), Usual, 59, 300, out takeover), "at the cap");
            Assert.IsFalse(takeover);
        }

        [TestMethod]
        public void AMemberPastTheCap_LooksTakenOver_AndChangesNothing()
        {
            Assert.IsNull(TrustLearning.Raised((20, 7), Usual, 60, 30, out var takeover));
            Assert.IsTrue(takeover);
            Assert.AreEqual((1000, 60), TrustLearning.Cap((900, 40)));
        }

        [TestMethod]
        public void Eased_GoesAQuarterBack_AndEndsAtTheUsual()
        {
            Assert.AreEqual(((int?)35, (int?)12), TrustLearning.Eased((40, 14), Usual));
            Assert.AreEqual(((int?)null, (int?)null), TrustLearning.Eased((21, 8), Usual));
            Assert.AreEqual(((int?)null, (int?)null), TrustLearning.Eased((15, null), Usual), "the usual level rose past it");
        }

        private static List<ScoredDecision> Decisions(params (int Score, bool Bad, int Count)[] groups) =>
            groups.SelectMany(g => Enumerable.Repeat(new ScoredDecision(g.Score, g.Bad), g.Count)).ToList();

        [TestMethod]
        public void NextReportAt_MovesOneStep_TowardsWhereMostDecisionsWereRemovals()
        {
            Assert.IsNull(TrustLearning.NextReportAt(Decisions((4, true, 9)), 3), "too few decisions");
            Assert.AreEqual(2, TrustLearning.NextReportAt(Decisions((2, true, 8), (4, false, 2)), 3), "low scores were spam: lower");
            Assert.IsNull(TrustLearning.NextReportAt(Decisions((2, true, 8), (4, false, 2)), 2), "there already");
            Assert.AreEqual(4, TrustLearning.NextReportAt(Decisions((3, false, 8), (6, true, 6)), 3), "most at 3 were fine: higher, one step");
            Assert.AreEqual(4, TrustLearning.NextReportAt(Decisions((3, false, 10), (3, true, 1)), 3), "nothing reaches the bar: one up");
            Assert.AreEqual(8, TrustLearning.NextReportAt(Decisions((3, false, 10)), 8) ?? 8, "never past 8");
        }
    }
}
