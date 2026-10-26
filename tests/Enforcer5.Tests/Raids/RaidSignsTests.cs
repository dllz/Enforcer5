using System;
using Enforcer5.Raids;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Raids
{
    [TestClass]
    public class RaidSignsTests
    {
        private static readonly DateTime Now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        private readonly RaidLimits _limits = new RaidLimits();

        [TestMethod]
        public void AWaveOfJoins_AloneIsOnlyPossible()
        {
            var signs = new RaidSigns();
            for (var i = 0; i < 15; i++) signs.Joined(i, Now.AddSeconds(i));

            var (score, reasons) = signs.Score(_limits, null);

            Assert.AreEqual(2, score);
            CollectionAssert.AreEqual(new[] { "joins" }, reasons);
            Assert.IsTrue(score >= _limits.PossibleScore && score < _limits.RaidScore);
        }

        [TestMethod]
        public void AWaveWithTheSameContent_IsARaid()
        {
            var signs = new RaidSigns();
            for (var i = 0; i < 15; i++) signs.Joined(i, Now.AddSeconds(i));
            signs.SameContentUsers = 3;

            Assert.AreEqual(5, signs.Score(_limits, null).Score);
        }

        [TestMethod]
        public void ABusyGroup_NeedsABiggerWave()
        {
            var signs = new RaidSigns();
            for (var i = 0; i < 20; i++) signs.Joined(i, Now.AddSeconds(i));

            // 120 joins an hour is two a minute: a wave there is forty.
            Assert.AreEqual(0, signs.Score(_limits, 120).Score);
            Assert.AreEqual(2, signs.Score(_limits, 10).Score);
        }

        [TestMethod]
        public void SignsOlderThanTheWindow_AreForgotten()
        {
            var signs = new RaidSigns();
            for (var i = 0; i < 3; i++) signs.Flooded(i, Now);
            Assert.AreEqual(2, signs.Score(_limits, null).Score);

            signs.Trim(Now.AddSeconds(61), _limits.Window);

            Assert.AreEqual(0, signs.Score(_limits, null).Score);
        }

        [TestMethod]
        public void TheSameFlooderTwice_IsOneFlooder()
        {
            var signs = new RaidSigns();
            signs.Flooded(1, Now);
            signs.Flooded(1, Now.AddSeconds(1));
            signs.Flooded(2, Now);
            Assert.AreEqual(0, signs.Score(_limits, null).Score);
        }
    }
}
