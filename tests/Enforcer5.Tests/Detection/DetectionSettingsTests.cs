using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Detection;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Enforcer5.Tests.Harness;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using StackExchange.Redis;

namespace Enforcer5.Tests.Detection
{
    /// <summary>
    /// The import of the old Redis settings (Detection step 2): every encoding and quirk the checks
    /// relied on comes out as the same behaviour. No database needed.
    /// </summary>
    [TestClass]
    [TestCategory("Detection")]
    public class LegacySettingsTests
    {
        private static HashEntry[] Hash(params (string Name, string Value)[] fields) =>
            fields.Select(f => new HashEntry(f.Name, f.Value)).ToArray();

        private static LegacySettings.Stored Stored(
            HashEntry[] settings = null, HashEntry[] flood = null, HashEntry[] floodExceptions = null,
            HashEntry[] media = null, HashEntry[] characters = null, HashEntry[] nameLength = null,
            HashEntry[] textLength = null, HashEntry[] warnSettings = null, HashEntry[] otherSettings = null,
            IReadOnlyList<InlineBotBlock> inlineBots = null, IReadOnlyList<string> stickerSets = null,
            bool channelPosts = false, IReadOnlyList<AllowedChannel> allowed = null) =>
            new LegacySettings.Stored(settings ?? Hash(), flood ?? Hash(), floodExceptions ?? Hash(), media ?? Hash(),
                characters ?? Hash(), nameLength ?? Hash(), textLength ?? Hash(), warnSettings ?? Hash(),
                otherSettings ?? Hash(), inlineBots ?? Array.Empty<InlineBotBlock>(), stickerSets ?? Array.Empty<string>(),
                channelPosts, allowed ?? Array.Empty<AllowedChannel>());

        [TestMethod]
        public void NothingStored_IsWhatTheChecksDidWithEmptyHashes()
        {
            var policy = LegacySettings.Map(Stored());
            Assert.IsTrue(policy.SameSettingsAs(DetectionPolicy.Empty));
            Assert.IsTrue(policy.Flood.Enabled, "a missing Flood field never switched flood off");
            Assert.IsNull(policy.Flood.MaxMessages, "but without a limit flood did nothing");
            Assert.IsFalse(policy.MuteOnJoin);
            Assert.IsFalse(policy.TextLength.Enabled);
            Assert.AreEqual(2, policy.Media.EffectiveWarnLimit);
            Assert.AreEqual(Methods.DefaultMaxWarns, policy.Warns.MaxWarns);
            Assert.AreEqual("kick", policy.Warns.Action);
            Assert.AreEqual(Duration.DefaultMinutes, policy.TempbanMinutes);
        }

        [TestMethod]
        public void Flood_YesMeansOff_AndTheLimitAndActionAreReadAsTheCheckDid()
        {
            Assert.IsFalse(LegacySettings.Map(Stored(settings: Hash(("Flood", "yes")))).Flood.Enabled);
            Assert.IsTrue(LegacySettings.Map(Stored(settings: Hash(("Flood", "no")))).Flood.Enabled);

            var flood = LegacySettings.Map(Stored(flood: Hash(("MaxFlood", "4"), ("ActionFlood", "tempban")))).Flood;
            Assert.AreEqual(4, flood.MaxMessages);
            Assert.AreEqual("tempban", flood.Action);

            flood = LegacySettings.Map(Stored(flood: Hash(("MaxFlood", "five"), ("ActionFlood", "Kick")))).Flood;
            Assert.IsNull(flood.MaxMessages, "an unreadable limit turned flood off");
            Assert.IsNull(flood.Action, "the switch was case-sensitive: Kick did nothing");
        }

        [TestMethod]
        public void FloodExceptions_NoMeansNotCounted_AnythingElseCounts()
        {
            var flood = LegacySettings.Map(Stored(floodExceptions: Hash(("image", "no"), ("video", "yes"), ("gif", "maybe")))).Flood;
            Assert.IsTrue(flood.IsExempt("image"));
            Assert.IsFalse(flood.IsExempt("video"));
            Assert.IsFalse(flood.IsExempt("gif"));
            Assert.IsFalse(flood.IsExempt("text"), "a type never set counts");
        }

        [TestMethod]
        public void MuteOnJoin_NoMeansOn()
        {
            Assert.IsTrue(LegacySettings.Map(Stored(settings: Hash(("MuteOnJoin", "no")))).MuteOnJoin);
            Assert.IsFalse(LegacySettings.Map(Stored(settings: Hash(("MuteOnJoin", "yes")))).MuteOnJoin);
            Assert.IsFalse(LegacySettings.Map(Stored()).MuteOnJoin);
        }

        [TestMethod]
        public void Media_BlockedTypes_TheActionTheSwitchKnew_AndTheStrikeLimit()
        {
            var media = LegacySettings.Map(Stored(
                media: Hash(("image", "blocked"), ("video", "allowed"), ("action", "ban"), ("link", "blocked")),
                warnSettings: Hash(("mediamax", "3")))).Media;
            Assert.IsTrue(media.IsBlocked("image"));
            Assert.IsTrue(media.IsBlocked("link"));
            Assert.IsFalse(media.IsBlocked("video"));
            Assert.IsFalse(media.IsBlocked("action"), "action is not a type");
            Assert.AreEqual("ban", media.Action);
            Assert.AreEqual(3, media.EffectiveWarnLimit);

            media = LegacySettings.Map(Stored(media: Hash(("action", "warn")), warnSettings: Hash(("mediamax", "lots")))).Media;
            Assert.IsNull(media.Action, "the media switch had no warn");
            Assert.AreEqual(2, media.EffectiveWarnLimit, "missing or unreadable was 2");
        }

        [TestMethod]
        public void Scripts_AllowedIsNoAction_AndRtlKeepsATempbanItNeverActedOn()
        {
            var scripts = LegacySettings.Map(Stored(characters: Hash(("Arab", "allowed"), ("Rtl", "tempban")))).Scripts;
            Assert.IsNull(scripts.Arabic);
            Assert.AreEqual("tempban", scripts.Rtl);
            scripts = LegacySettings.Map(Stored(characters: Hash(("Arab", "kick"), ("Rtl", "whatever")))).Scripts;
            Assert.AreEqual("kick", scripts.Arabic);
            Assert.IsNull(scripts.Rtl);
        }

        [TestMethod]
        public void Length_CapitalWarnIsWarn_LowercaseWarnWasNoAction_AndBadLimitsAreTheDefault()
        {
            var text = LegacySettings.Map(Stored(textLength: Hash(("enabled", "yes"), ("maxlength", "100"), ("maxlines", "0"), ("action", "Warn")))).TextLength;
            Assert.IsTrue(text.Enabled);
            Assert.AreEqual(100, text.MaxCharacters);
            Assert.IsNull(text.MaxLines, "0 was the default");
            Assert.AreEqual("warn", text.Action);

            var name = LegacySettings.Map(Stored(nameLength: Hash(("enabled", "Yes"), ("maxlength", "x"), ("action", "warn")))).NameLength;
            Assert.IsFalse(name.Enabled, "only \"yes\" switched it on");
            Assert.IsNull(name.MaxCharacters);
            Assert.IsNull(name.Action, "lowercase warn fell to \"no action chosen\"");
        }

        [TestMethod]
        public void Warns_AndTempLengths_AreReadAsTheirReadersDid()
        {
            var policy = LegacySettings.Map(Stored(warnSettings: Hash(("max", "3"), ("type", "ban")),
                otherSettings: Hash(("tempbanTime", "60"), ("tempMuteTime", "30"))));
            Assert.AreEqual(3, policy.Warns.MaxWarns);
            Assert.AreEqual("ban", policy.Warns.Action);
            Assert.AreEqual(60, policy.TempbanMinutes);
            Assert.AreEqual(30, policy.TempmuteMinutes);

            policy = LegacySettings.Map(Stored(warnSettings: Hash(("max", "0"), ("type", "Ban")),
                otherSettings: Hash(("tempbanTime", "-5"), ("tempMuteTime", int.MaxValue.ToString()))));
            Assert.AreEqual(Methods.DefaultMaxWarns, policy.Warns.MaxWarns);
            Assert.AreEqual("kick", policy.Warns.Action);
            Assert.AreEqual(Duration.DefaultMinutes, policy.TempbanMinutes);
            Assert.AreEqual(Duration.DefaultMinutes, policy.TempmuteMinutes, "past Telegram's limit is the default");
        }

        [TestMethod]
        public void Blocklists_AndChannelPosts_ComeAcross_WithoutDuplicates()
        {
            var bot = new InlineBotBlock(InlineBotBlockKind.Username, "gif");
            var policy = LegacySettings.Map(Stored(inlineBots: new[] { bot, bot }, stickerSets: new[] { "pack", "pack" },
                channelPosts: true, allowed: new[] { new AllowedChannel(-100, "News"), new AllowedChannel(-100, "News") }));
            CollectionAssert.AreEqual(new[] { bot }, policy.InlineBots.ToArray());
            CollectionAssert.AreEqual(new[] { "pack" }, policy.StickerSets.ToArray());
            Assert.IsTrue(policy.ChannelPosts.Enabled);
            Assert.IsTrue(policy.ChannelPosts.IsAllowed(-100));
            Assert.AreEqual(1, policy.ChannelPosts.Allowed.Count);
        }

        [TestMethod]
        public void NewGroupDefaults_AreWhatGenerateSettingsWroteToRedis()
        {
            // The values Service.GenerateSettings wrote before Detection step 2.
            var generated = LegacySettings.Map(Stored(
                settings: Hash(("Flood", "yes")),
                flood: Hash(("MaxFlood", "5"), ("ActionFlood", "kick")),
                characters: Hash(("Arab", "allowed"), ("Rtl", "allowed")),
                floodExceptions: Hash(("image", "yes"), ("video", "yes"), ("sticker", "yes"), ("gif", "yes")),
                warnSettings: Hash(("type", "ban"), ("max", "5"), ("mediamax", "3")),
                media: Hash(("image", "allowed"), ("audio", "allowed"), ("video", "allowed"), ("sticker", "allowed"),
                    ("gif", "allowed"), ("voice", "allowed"), ("contact", "allowed"), ("file", "allowed"),
                    ("link", "allowed"), ("action", "tempban")),
                textLength: Hash(("enabled", "yes"), ("maxlength", "1024"), ("maxlines", "50"), ("action", "kick")),
                nameLength: Hash(("enabled", "yes"), ("maxlength", "50"), ("action", "kick"))));
            Assert.IsTrue(DetectionPolicy.Empty.WithNewGroupDefaults().SameSettingsAs(generated));
        }

        [TestMethod]
        public void NewGroupDefaults_KeepWhatGenerateSettingsNeverTouched()
        {
            var bot = new InlineBotBlock(InlineBotBlockKind.Username, "gif");
            var before = DetectionPolicy.Empty with
            {
                MuteOnJoin = true,
                TempbanMinutes = 60,
                InlineBots = new[] { bot },
                Flood = DetectionPolicy.Empty.Flood with { Counted = new Dictionary<string, bool> { ["text"] = false } }
            };
            var after = before.WithNewGroupDefaults();
            Assert.IsTrue(after.MuteOnJoin);
            Assert.AreEqual(60, after.TempbanMinutes);
            CollectionAssert.AreEqual(new[] { bot }, after.InlineBots.ToArray());
            Assert.IsTrue(after.Flood.IsExempt("text"));
            Assert.IsFalse(after.Flood.Enabled);
        }
    }

    [TestClass]
    [TestCategory("Detection")]
    public class DetectionPolicyJsonTests
    {
        internal static DetectionPolicy Everything() => DetectionPolicy.Empty.WithNewGroupDefaults() with
        {
            Version = 7,
            MuteOnJoin = true,
            Scripts = new ScriptSettings("ban", "kick"),
            Media = new MediaSettings(new Dictionary<string, bool> { ["image"] = true, ["text"] = false }, "kick", null),
            NameLength = new LengthSettings(true, 30, null, null),
            Warns = new WarnSettings(3, WarnSettings.Ban),
            TempbanMinutes = 90,
            TempmuteMinutes = 15,
            InlineBots = new[] { new InlineBotBlock(InlineBotBlockKind.Username, "gif"), new InlineBotBlock(InlineBotBlockKind.Pattern, "^spam.*bot$") },
            StickerSets = new[] { "badpack", "worsepack" },
            ChannelPosts = new ChannelPostSettings(true, new[] { new AllowedChannel(-1001, "News"), new AllowedChannel(-1002, "") }),
            Engine = new EngineSettings(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero), 4, 8, true,
                new Dictionary<string, SignPolicy>
                {
                    ["link_outside"] = new SignPolicy(true, "delete", 3, Delete: true),
                    ["name_keyword"] = new SignPolicy(false, "none", 0, Report: true)
                })
        };

        [TestMethod]
        public void RoundTrips()
        {
            var policy = Everything();
            var back = DetectionPolicyJson.Deserialize(DetectionPolicyJson.Serialize(policy));
            Assert.IsNotNull(back);
            Assert.AreEqual(7, back.Version);
            Assert.IsTrue(back.SameSettingsAs(policy));
            Assert.IsTrue(back.Media.IsBlocked("image"));
            Assert.AreEqual(InlineBotBlockKind.Pattern, back.InlineBots[1].Kind);
        }

        [TestMethod]
        public void AnythingElse_IsNoPolicy()
        {
            Assert.IsNull(DetectionPolicyJson.Deserialize(null));
            Assert.IsNull(DetectionPolicyJson.Deserialize("not json"));
            Assert.IsNull(DetectionPolicyJson.Deserialize("{}"));
            Assert.IsNull(DetectionPolicyJson.Deserialize("{\"Version\":3}"));
        }
    }

    /// <summary>
    /// <see cref="DetectionSettings"/> without PostgreSQL: the memory, the Redis cache and the old
    /// hashes, on a simulated clock.
    /// </summary>
    [TestClass]
    [TestCategory("Detection")]
    [DoNotParallelize] // Clock, Redis.db and DetectionSettings are static
    public class DetectionSettingsCacheTests
    {
        private const long ChatId = -1009876543210;
        private FakeClock _clock;
        private FakeRedisHashes _hashes;
        private FakeRedisKeys _keys;

        [TestInitialize]
        public void Init()
        {
            _clock = FakeClock.StartingNow();
            Clock.UseForTests(_clock);
            _hashes = new FakeRedisHashes();
            _keys = new FakeRedisKeys(_clock, _hashes);
            var redis = new Mock<IDatabase>();
            _hashes.Install(redis);
            _keys.Install(redis);
            Redis.UseForTests(redis.Object);
            Enforcer5.Storage.PostgresStorage.UseForTests(null);
            DetectionSettings.ResetForTests();
        }

        [TestCleanup]
        public void Cleanup()
        {
            DetectionSettings.ResetForTests();
            Clock.UseForTests(null);
        }

        private void Cache(DetectionPolicy policy)
        {
            _keys[DetectionSettings.CacheKey(ChatId)] = DetectionPolicyJson.Serialize(policy);
            _keys[DetectionSettings.VersionKey(ChatId)] = policy.Version.ToString();
        }

        [TestMethod]
        public async Task WithoutCacheOrPostgres_TheOldHashesServe()
        {
            _hashes[$"chat:{ChatId}:settings", "MuteOnJoin"] = "no";
            var policy = await DetectionSettings.GetAsync(ChatId);
            Assert.IsTrue(policy.MuteOnJoin);
            Assert.AreEqual(0, _hashes.All($"chat:{ChatId}:settings").Count(f => f.Key != "MuteOnJoin"), "nothing written back");
        }

        [TestMethod]
        public async Task TheCache_WinsOverTheOldHashes()
        {
            _hashes[$"chat:{ChatId}:settings", "MuteOnJoin"] = "no";
            Cache(DetectionPolicy.Empty with { Version = 4 });
            var policy = await DetectionSettings.GetAsync(ChatId);
            Assert.IsFalse(policy.MuteOnJoin);
            Assert.AreEqual(4, policy.Version);
        }

        [TestMethod]
        public async Task ANewerCachedVersion_IsPickedUp_AfterTheRecheckInterval()
        {
            Cache(DetectionPolicy.Empty with { Version = 1 });
            Assert.IsFalse((await DetectionSettings.GetAsync(ChatId)).MuteOnJoin);

            // The other edition changes it.
            Cache(DetectionPolicy.Empty with { Version = 2, MuteOnJoin = true });
            Assert.IsFalse((await DetectionSettings.GetAsync(ChatId)).MuteOnJoin, "memory serves until the recheck");

            _clock.Advance(DetectionSettings.RecheckAfter);
            Assert.IsTrue((await DetectionSettings.GetAsync(ChatId)).MuteOnJoin);
        }

        [TestMethod]
        public async Task AnOlderOrUnreadableCache_DoesNotReplaceWhatIsInMemory()
        {
            Cache(DetectionPolicy.Empty with { Version = 3, MuteOnJoin = true });
            await DetectionSettings.GetAsync(ChatId);
            _keys[DetectionSettings.CacheKey(ChatId)] = "garbage";
            _keys[DetectionSettings.VersionKey(ChatId)] = "9";
            _clock.Advance(DetectionSettings.RecheckAfter);
            Assert.IsTrue((await DetectionSettings.GetAsync(ChatId)).MuteOnJoin);
        }

        [TestMethod]
        public void ChangingSettings_WithoutPostgres_Fails_AndChangesNothing()
        {
            Assert.ThrowsException<DetectionSettingsUnavailableException>(
                () => DetectionSettings.Update(ChatId, p => p with { MuteOnJoin = true }));
            Assert.IsNull(DetectionSettings.TryUpdate(ChatId, p => p with { MuteOnJoin = true }));
            Assert.IsNull(_keys[DetectionSettings.CacheKey(ChatId)]);
        }
    }
}

namespace Enforcer5.Tests.Detection
{
    using Telegram.Bot.Types;

    /// <summary>What an admin did in the Telegram app, from a member's status before and after.</summary>
    [TestClass]
    [TestCategory("Detection")]
    public class DecisionChangeTests
    {
        private static readonly User U = new User { Id = 5, FirstName = "M" };
        private static readonly DateTime Now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        private static ChatMember Member => new ChatMemberMember { User = U };
        private static ChatMember Left => new ChatMemberLeft { User = U };
        private static ChatMember Banned(DateTime? until = null) => new ChatMemberBanned { User = U, UntilDate = until };
        private static ChatMember Restricted(bool canSend, bool isMember = true, DateTime? until = null) =>
            new ChatMemberRestricted { User = U, CanSendMessages = canSend, IsMember = isMember, UntilDate = until };

        [TestMethod]
        public void Changes()
        {
            Assert.AreEqual("ban", Enforcer5.Detection.Decisions.Change(Member, Banned(), Now));
            Assert.AreEqual("ban", Enforcer5.Detection.Decisions.Change(Member, Banned(Now.AddDays(400)), Now), "over a year is forever");
            Assert.AreEqual("tempban", Enforcer5.Detection.Decisions.Change(Member, Banned(Now.AddDays(1)), Now));
            Assert.AreEqual("unban", Enforcer5.Detection.Decisions.Change(Banned(), Left, Now));
            Assert.AreEqual("kick", Enforcer5.Detection.Decisions.Change(Member, Left, Now));
            Assert.AreEqual("mute", Enforcer5.Detection.Decisions.Change(Member, Restricted(false), Now));
            Assert.AreEqual("unmute", Enforcer5.Detection.Decisions.Change(Restricted(false), Member, Now));
            Assert.AreEqual("unmute", Enforcer5.Detection.Decisions.Change(Restricted(false), Restricted(true), Now));
            Assert.IsNull(Enforcer5.Detection.Decisions.Change(Left, Member, Now), "a join");
            Assert.IsNull(Enforcer5.Detection.Decisions.Change(Member, Restricted(true), Now), "no media is not a mute");
            Assert.IsNull(Enforcer5.Detection.Decisions.Change(Banned(), Banned(), Now));
        }

        [TestMethod]
        public void Stronger_FollowsTheOrderOfActions()
        {
            Assert.AreEqual("mute", Enforcer5.Detection.DetectionEngine.Stronger("score", "mute"));
            Assert.AreEqual("ban", Enforcer5.Detection.DetectionEngine.Stronger("ban", "mute"));
        }
    }
}
