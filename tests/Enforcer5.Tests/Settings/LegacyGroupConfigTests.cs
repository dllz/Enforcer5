using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Tests.Warns;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using StackExchange.Redis;

namespace Enforcer5.Tests.Settings
{
    /// <summary>How the old Redis keys read as a group's config (rollout step 15): the import's only source.</summary>
    [TestClass]
    public class LegacyGroupConfigTests
    {
        private const long Chat = -1001;

        private static Task<GroupConfig> Read(FakeRedis redis) => LegacyGroupConfig.ReadAsync(redis.Database, Chat);

        private static FakeRedis Redis()
        {
            var redis = new FakeRedis();
            redis.Mock.Setup(d => d.SetContainsAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisKey k, RedisValue v, CommandFlags _) => redis.Hash(k).ContainsKey(v));
            return redis;
        }

        [TestMethod]
        public async Task TheInvertedOldFlags_ReadTheRightWayRound()
        {
            var redis = Redis();
            var settings = redis.Hash($"chat:{Chat}:settings");
            settings["Welcome"] = "yes"; // off
            settings["DeleteLastWelcome"] = "no"; // on
            settings["Rules"] = "yes";
            settings["About"] = "no";
            settings["Report"] = "yes"; // the menu's "off", which nothing read
            settings["Flagged"] = "yes";

            var config = await Read(redis);

            Assert.IsFalse(config.Flag(ConfigFlag.Welcome));
            Assert.IsTrue(config.Flag(ConfigFlag.DeleteLastWelcome));
            Assert.IsTrue(config.Flag(ConfigFlag.RulesPrivate));
            Assert.IsFalse(config.Flag(ConfigFlag.AboutPrivate));
            Assert.IsTrue(config.Flag(ConfigFlag.Reports), "reports are imported as on (Daniel, 2026-11-06)");
        }

        [TestMethod]
        public async Task MissingFields_AreWhatTheOldCodeDidWithThem()
        {
            var config = await Read(Redis());

            Assert.IsTrue(config.Flag(ConfigFlag.Welcome), "no Welcome field welcomed");
            Assert.IsFalse(config.Flag(ConfigFlag.DeleteLastWelcome));
            Assert.IsFalse(config.Flag(ConfigFlag.RulesPrivate), "answered in the group");
            Assert.IsTrue(config.Flag(ConfigFlag.HelpPrivate), "the last one-time migration set a missing Help to yes");
            Assert.AreEqual(new WelcomeConfig("composed", null, null, null), config.Welcome, "never set: nothing is sent");
            Assert.IsNull(config.LogChat);
        }

        [TestMethod]
        public async Task TheLogChannel_CountsOnlyWhileTheGroupIsListed()
        {
            var redis = Redis();
            redis.Hash($"chat:{Chat}:settings")["logchat"] = "-100777";
            Assert.IsNull((await Read(redis)).LogChat, "removed with /dellogchannel, which left the id");
            redis.Hash("logChatGroups")[Chat] = 1;
            Assert.AreEqual(-100777L, (await Read(redis)).LogChat);
        }

        [DataTestMethod]
        [DataRow("custom", "Hi $name", "true", "gif1", "custom", "Hi $name", "gif1")]
        [DataRow("custom", "Hi", "false", "old-gif", "custom", "Hi", null)]
        [DataRow("media", "", null, "gif2", "media", null, "gif2")]
        [DataRow("media", "gif-before-2017", null, null, "media", null, "gif-before-2017")]
        [DataRow("composed", "ra", "true", "gif3", "composed", "ra", null)]
        public void Welcomes_KeepOnlyWhatWasSent(string type, string content, string hasMedia, string media,
            string expectedType, string expectedContent, string expectedFile)
        {
            var hash = new System.Collections.Generic.List<HashEntry> { new("type", type), new("content", content) };
            if (hasMedia != null) hash.Add(new HashEntry("hasmedia", hasMedia));
            if (media != null) hash.Add(new HashEntry("media", media));

            var welcome = LegacyGroupConfig.Welcome(hash.ToArray());

            Assert.AreEqual((expectedType, expectedContent, expectedFile), (welcome.Type, welcome.Content, welcome.MediaFileId));
            Assert.AreEqual(expectedFile == null ? null : "legacy_unknown", welcome.MediaFileEdition);
        }

        [TestMethod]
        public async Task TextsAndExtras_ComeAlong_AndLeftoverSideFieldsAreDropped()
        {
            var redis = Redis();
            redis.Strings[$"chat:{Chat}:rules"] = ("Be nice", null);
            redis.Hash($"chat:{Chat}links")["link"] = "no";
            var extras = redis.Hash($"chat:{Chat}:extra");
            extras["#hi"] = "Hello";
            extras["#cat"] = "###file_id!photo###:PHOTO";
            extras["#dance"] = "Look";
            extras["#bad"] = "###file_id!photo###";
            redis.Hash($"chat:{Chat}:extra:#hi")["caption"] = "stale, from a file it once was";
            redis.Hash($"chat:{Chat}:extra:#cat")["caption"] = "Kitty";
            redis.Hash($"chat:{Chat}:extra:#cat")["mediaid"] = "stale gif";
            redis.Hash($"chat:{Chat}:extra:#dance")["mediaid"] = "GIF";

            var config = await Read(redis);

            Assert.AreEqual("Be nice", config.Text(ContentKind.Rules));
            Assert.IsNull(config.Text(ContentKind.Link), "\"no\" is no link");
            Assert.AreEqual(ExtraConfig.OfText("Hello"), config.Extra("#hi"));
            Assert.AreEqual(ExtraConfig.OfFile("photo", "PHOTO", "legacy_unknown", "Kitty"), config.Extra("#cat"));
            Assert.AreEqual(ExtraConfig.OfText("Look", "GIF", "legacy_unknown"), config.Extra("#dance"));
            Assert.IsNull(config.Extra("#bad"), "unreadable");
            Assert.AreEqual("###file_id!photo###:PHOTO", config.Extra("#cat").Raw, "the old form, for the data recovery");
        }

        [TestMethod]
        public void Changes_ApplyInMemory_AndAResetKeepsTextsAndExtras()
        {
            var config = GroupConfig.NewGroup
                .Apply(ConfigChange.SetFlag(ConfigFlag.Welcome, true))
                .Apply(ConfigChange.SetContent(ContentKind.About, "We play"))
                .Apply(ConfigChange.SetExtra("#x", ExtraConfig.OfText("y")))
                .Apply(ConfigChange.SetWelcome(new WelcomeConfig("custom", "Hi", null, null)))
                .Apply(ConfigChange.Reset());

            Assert.IsFalse(config.Flag(ConfigFlag.Welcome));
            Assert.AreEqual(WelcomeConfig.Default, config.Welcome);
            Assert.AreEqual("We play", config.Text(ContentKind.About));
            Assert.IsNotNull(config.Extra("#x"));
            Assert.IsTrue(config.Apply(ConfigChange.SetExtra("#x", null)).Extras.IsEmpty);
        }
    }

    internal static class GroupConfigTestExtensions
    {
        internal static GroupConfig Apply(this GroupConfig config, ConfigChange change) => change.ApplyTo(config);
    }
}
