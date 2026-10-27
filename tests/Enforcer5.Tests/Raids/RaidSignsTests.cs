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

        [TestMethod]
        public void TheSamePlainText_IsAnEcho_NotHarm()
        {
            var signs = new RaidSigns { EchoUsers = 5 };
            var (score, reasons) = signs.Score(_limits, null);
            Assert.AreEqual(1, score);
            Assert.IsFalse(RaidSigns.Harmful(reasons));
        }

        [TestMethod]
        public void ManyPostingTheSameSpam_IsARaidOnItsOwn()
        {
            var signs = new RaidSigns { SameContentUsers = 6 };
            var (score, reasons) = signs.Score(_limits, null);
            Assert.AreEqual(4, score);
            Assert.IsTrue(RaidSigns.Harmful(reasons));
        }

        [TestMethod]
        public void AJoinNowAndThen_DoesNotKeepRaidModeGoing()
        {
            var signs = new RaidSigns();
            signs.Joined(1, Now);
            Assert.IsFalse(signs.JoinsKeepItGoing(_limits, null));
            signs.Joined(2, Now);
            signs.Joined(3, Now);
            Assert.IsTrue(signs.JoinsKeepItGoing(_limits, null));
            Assert.IsFalse(signs.JoinsKeepItGoing(_limits, 120), "a group with two joins a minute needs more");
        }

        [TestMethod]
        public void AnApprovedJoin_IsTakenBack()
        {
            var signs = new RaidSigns();
            signs.Joined(1, Now);
            signs.Joined(2, Now);
            signs.Vetted(1);
            Assert.AreEqual(1, signs.JoinCount);
            Assert.IsNull(signs.JoinedAt(1, Now));
        }

        [TestMethod]
        public void AutoMode_ActsOnPeopleOnlyForHarm()
        {
            Assert.AreEqual(RaidActions.Quarantine, RaidGuard.AutoActions(3, new[] { "joins", "echo" }));
            Assert.AreEqual(RaidActions.Quarantine | RaidActions.MuteSuspects, RaidGuard.AutoActions(5, new[] { "joins", "echo", "posted_on_joining", "reports" }));
            Assert.AreEqual(RaidActions.Quarantine | RaidActions.MuteSuspects, RaidGuard.AutoActions(3, new[] { "same_content" }));
            Assert.AreEqual(RaidActions.All, RaidGuard.AutoActions(4, new[] { "same_content", "same_content_many" }));
        }
    }
}
