using System;
using Enforcer5.Helpers;
using Enforcer5.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StackExchange.Redis;

namespace Enforcer5.Tests.RateLimits
{
    [TestClass]
    [DoNotParallelize] // Clock is static
    public class GroupSettingsTests
    {
        private FakeClock _clock;
        private static readonly GroupSettings.Snapshot Some = new(new HashEntry[0]);

        [TestInitialize]
        public void Init()
        {
            _clock = FakeClock.StartingNow();
            Clock.UseForTests(_clock);
            GroupSettings.ResetForTests();
        }

        [TestCleanup]
        public void Cleanup()
        {
            Clock.UseForTests(null);
            GroupSettings.ResetForTests();
        }

        [TestMethod]
        public void AQuietGroup_ReadsItsSettingsEveryMessage()
        {
            for (var i = 0; i < GroupSettings.BusyMessages; i++)
            {
                Assert.IsNull(GroupSettings.Get(-1));
                GroupSettings.Store(-1, Some);
            }
        }

        [TestMethod]
        public void ABusyGroup_UsesASnapshot_AtMostTenSecondsOld()
        {
            for (var i = 0; i < GroupSettings.BusyMessages; i++)
            {
                GroupSettings.Get(-1);
                GroupSettings.Store(-1, Some);
            }
            Assert.AreSame(Some, GroupSettings.Get(-1), "busy: from the snapshot");

            _clock.Advance(TimeSpan.FromSeconds(10));
            Assert.IsNull(GroupSettings.Get(-1), "the snapshot is too old, and the group quiet again");
        }
    }
}
