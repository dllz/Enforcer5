using System;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Tests.Warns;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Mutes
{
    /// <summary>The old Redis mute keys, read once per group for the import (rollout step 16). The rest: Storage/MuteStoreTests.</summary>
    [TestClass]
    public class LegacyMutesTests
    {
        private const long Chat = -1001234567890;
        private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public async Task Reads_TheThreeKeys_OneKindPerMember()
        {
            var redis = new FakeRedis();
            // The keys predate the repository and hold live data.
            redis.Set($"chat:{Chat}:muted").Add(1);
            redis.Set($"chat:{Chat}:muted").Add("garbage");
            redis.Set($"chat:{Chat}:mutedJoiners").Add(2);
            redis.Set($"chat:{Chat}:mutedJoiners").Add(1);
            redis.Set($"chat:{Chat}:mutedJoiners").Add(3);
            redis.Hash($"chat:{Chat}:tempmuted")[3] = new DateTimeOffset(Now.AddHours(1)).ToUnixTimeSeconds();
            redis.Hash($"chat:{Chat}:tempmuted")[4] = new DateTimeOffset(Now.AddHours(-1)).ToUnixTimeSeconds();
            redis.Hash($"chat:{Chat}:tempmuted")["x"] = 5;

            var mutes = await new LegacyMutes(() => redis.Database).ReadAsync(Chat, Now);

            CollectionAssert.AreEquivalent(new[]
            {
                new LegacyMute(1, "mute", null),
                new LegacyMute(2, "joiner", null),
                new LegacyMute(3, "tempmute", Now.AddHours(1))
            }, mutes.ToArray(), "a mute for good beats the others, a tempmute a join mute; ended and unreadable ones are left out");
        }

        [TestMethod]
        public void CompositionRoot_UsesTheStore()
        {
            Assert.IsInstanceOfType(Repositories.Mutes, typeof(MuteStore));
            Assert.IsInstanceOfType(Repositories.Tempbans, typeof(TempbanStore));
        }
    }
}
