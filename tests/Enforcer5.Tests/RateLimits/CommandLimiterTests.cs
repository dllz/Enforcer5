using System;
using System.Linq;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.RateLimits
{
    [TestClass]
    public class CommandLimiterTests
    {
        private const long User = 42;
        private const long Admin = 7;
        private const long Group = -100;
        private DateTime _now;
        private CommandLimiter _limiter;

        [TestInitialize]
        public void SetUp()
        {
            _now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            _limiter = new CommandLimiter(new CommandLimits(), () => _now);
        }

        private CommandDecision Command(long user = User, long group = Group) =>
            _limiter.Check(user, group, 1, () => user == Admin);

        [TestMethod]
        public void NormalUse_IsNeverThrottled()
        {
            for (var i = 0; i < 100; i++)
            {
                Assert.IsTrue(Command().Allowed, $"command {i}");
                _now = _now.AddSeconds(3);
            }
        }

        [TestMethod]
        public void EmptyingTheBucket_ToldOnce_ThenSilent()
        {
            for (var i = 0; i < 4; i++) Assert.IsTrue(Command().Allowed);

            var first = Command();
            var second = Command();

            Assert.AreEqual((false, ThrottleScope.User, true), (first.Allowed, first.Scope, first.Notify));
            Assert.AreEqual(TimeSpan.FromSeconds(10), first.RetryIn);
            Assert.AreEqual((false, false), (second.Allowed, second.Notify));
        }

        [TestMethod]
        public void KeepingOnWhileThrottled_MeansTheSlowLane_NotABan()
        {
            for (var i = 0; i < 5; i++) Command(); // into a cooldown
            CommandDecision d = default;
            for (var i = 0; i < 15; i++) d = Command();

            Assert.AreEqual((ThrottleScope.SlowLane, true), (d.Scope, d.Notify), "told once on the way in");
            Assert.IsTrue(_limiter.InSlowLane(User) == true);

            // One answer every five minutes, every attempt in between dropped silently.
            _now = _now.AddMinutes(4);
            var early = Command();
            Assert.AreEqual((false, false), (early.Allowed, early.Notify));
            _now = _now.AddMinutes(1);
            var answered = Command();
            Assert.IsTrue(answered.Allowed && answered.FromSlowLane);
            Assert.IsFalse(Command().Allowed);
        }

        [TestMethod]
        public void TheSlowLane_EndsAfterFifteenQuietMinutes_AndTryingKeepsItGoing()
        {
            for (var i = 0; i < 20; i++) Command();
            Assert.IsTrue(_limiter.InSlowLane(User) == true);

            _now = _now.AddMinutes(14);
            Command(); // still trying: another fifteen from here
            _now = _now.AddMinutes(14);
            Assert.IsTrue(_limiter.InSlowLane(User) == true);
            _now = _now.AddMinutes(2);

            Assert.IsTrue(_limiter.InSlowLane(User) == false);
            Assert.IsTrue(Command().Allowed, "a legit user who stopped is back to normal");
        }

        [TestMethod]
        public void ALaterStay_ComesSooner_NeverLonger()
        {
            for (var i = 0; i < 20; i++) Command();
            _now = _now.AddMinutes(40); // out again
            Assert.IsTrue(Command().Allowed);

            for (var i = 0; i < 4; i++) Command(); // empty the bucket again
            var attempts = 0;
            CommandDecision d;
            do { d = Command(); attempts++; } while (d.Scope != ThrottleScope.SlowLane && attempts < 50);

            Assert.IsTrue(attempts <= 9, $"{attempts} attempts the second time");
            Assert.AreEqual(TimeSpan.FromMinutes(15), d.SlowLane.QuietUntilUtc - _now, "the same stay as the first time");
            Assert.AreEqual(2, d.SlowLane.Strikes);
        }

        [TestMethod]
        public void Admins_AreSlowedWhenWasting_ButNeverPutInTheSlowLane()
        {
            for (var i = 0; i < 40; i++) Command(Admin);

            Assert.IsFalse(_limiter.InSlowLane(Admin) == true);
            Assert.IsFalse(Command(Admin).Allowed, "but they are in a cooldown");
        }

        [TestMethod]
        public void RealEnforcement_GivesTheTokenBack()
        {
            // An admin banning fifty raiders, one command each, each a ban Telegram took.
            for (var i = 0; i < 50; i++)
            {
                Assert.IsTrue(Command(Admin).Allowed, $"ban {i}");
                _limiter.Refund(Admin, Group, 1);
            }
        }

        [TestMethod]
        public void ManyUsersAtOnce_PauseTheGroup_ForAllButItsAdmins()
        {
            CommandDecision paused = default;
            for (var c = 0; c < 60 && paused.Scope != ThrottleScope.Group; c++)
                paused = Command(1000 + c / 2); // thirty people, two commands each

            Assert.AreEqual((ThrottleScope.Group, true), (paused.Scope, paused.Notify));
            Assert.IsFalse(Command(5000).Allowed, "someone new waits too");
            Assert.IsTrue(Command(Admin).Allowed, "the admins can still act");
        }

        [TestMethod]
        public void SlowLaneFromRedis_IsKeptAfterARestart()
        {
            var stored = new SlowLaneState
            {
                QuietUntilUtc = _now.AddMinutes(10), NextAnswerUtc = _now.AddMinutes(3), Strikes = 1, StrikesSinceUtc = _now
            };

            var d = _limiter.Check(User, Group, 1, () => false, stored);

            Assert.IsFalse(d.Allowed);
            Assert.AreEqual(ThrottleScope.SlowLane, d.Scope);
        }

        [TestMethod]
        public void FormatWait_IsLanguageNeutral()
        {
            Assert.AreEqual("0:10", CommandLimiter.FormatWait(TimeSpan.FromSeconds(10)));
            Assert.AreEqual("2:00", CommandLimiter.FormatWait(TimeSpan.FromMinutes(2)));
            Assert.AreEqual("1:00:00", CommandLimiter.FormatWait(TimeSpan.FromHours(1)));
        }
    }
}
