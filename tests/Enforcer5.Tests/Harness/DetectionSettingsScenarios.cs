using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Data;
using Enforcer5.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Detection step 2: the automatic checks' settings live in PostgreSQL. A group's old Redis
    /// settings are imported on its first use, menus change PostgreSQL only, and Redis is a cache.
    /// Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Detection")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class DetectionSettingsScenarios
    {
        private const string Arabic = "سلام";

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static MemberState State(HarnessChat chat, HarnessUser user) => chat.Telegram.GetMember(chat.ChatId, user.Id);

        /// <summary>The settings menu in <paramref name="admin"/>'s private chat with the bot.</summary>
        private static Message Menu(HarnessChat chat, HarnessUser admin) => new Message
        {
            Id = chat.Telegram.NextMessageId(),
            Chat = new Chat { Id = admin.Id, Type = ChatType.Private },
            From = HarnessChat.BotUser,
            Text = "Settings"
        };

        private static Task<DetectionPolicy> Stored(HarnessChat chat) =>
            new PostgresDetectionSettingsRepository(() => chat.Database).LoadAsync(chat.ChatId);

        private static string[] CallbackAnswers(HarnessChat chat) =>
            chat.Telegram.Accepted.Where(c => c.Method == "answerCallbackQuery").Select(c => c.Text).ToArray();

        [TestMethod]
        public async Task TheFirstMessage_ImportsTheOldSettings_AndLeavesTheOldHashesAsTheyWere()
        {
            await using var chat = await HarnessChat.StartAsync();
            var id = chat.ChatId;
            chat.Hashes[$"chat:{id}:settings", "Flood"] = "no";
            chat.Hashes[$"chat:{id}:settings", "MuteOnJoin"] = "no";
            chat.Hashes[$"chat:{id}:flood", "MaxFlood"] = "4";
            chat.Hashes[$"chat:{id}:flood", "ActionFlood"] = "ban";
            chat.Hashes[$"chat:{id}:floodexceptions", "image"] = "no";
            chat.Hashes[$"chat:{id}:media", "video"] = "blocked";
            chat.Hashes[$"chat:{id}:media", "action"] = "kick";
            chat.Hashes[$"chat:{id}:char", "Arab"] = "tempban";
            chat.Hashes[$"chat:{id}:antitextlengthsettings", "enabled"] = "yes";
            chat.Hashes[$"chat:{id}:antitextlengthsettings", "action"] = "Warn";
            chat.Hashes[$"chat:{id}:warnsettings", "max"] = "3";
            chat.Hashes[$"chat:{id}:warnsettings", "type"] = "ban";
            chat.Hashes[$"chat:{id}:otherSettings", "tempbanTime"] = "60";
            chat.Keys.AddToSet($"chat:{id}:blockedinline", "u:spambot");
            chat.Keys.AddToSet($"chat:{id}:blockedstickersets", "badpack");
            chat.Hashes[$"chat:{id}:channelposts", "enabled"] = "yes";
            chat.Hashes[$"chat:{id}:allowedchannels", "-1009"] = "News";
            var before = new[] { "settings", "flood", "floodexceptions", "media", "char", "antitextlengthsettings", "warnsettings", "otherSettings", "channelposts", "allowedchannels" }
                .ToDictionary(h => h, h => string.Join(",", chat.Hashes.All($"chat:{id}:{h}").OrderBy(f => f.Key).Select(f => $"{f.Key}={f.Value}")));

            await chat.SendAsync(chat.Add("Talker"), "hello");

            var stored = await Stored(chat);
            Assert.IsNotNull(stored, "imported on the first message");
            Assert.AreEqual(1, stored.Version);
            Assert.IsTrue(stored.Flood.Enabled);
            Assert.AreEqual(4, stored.Flood.MaxMessages);
            Assert.AreEqual("ban", stored.Flood.Action);
            Assert.IsTrue(stored.Flood.IsExempt("image"));
            Assert.IsTrue(stored.MuteOnJoin);
            Assert.IsTrue(stored.Media.IsBlocked("video"));
            Assert.AreEqual("kick", stored.Media.Action);
            Assert.AreEqual("tempban", stored.Filters.PresetAction(FilterPresets.Arabic));
            Assert.AreEqual("warn", stored.TextLength.Action);
            Assert.AreEqual(3, stored.Warns.MaxWarns);
            Assert.AreEqual("ban", stored.Warns.Action);
            Assert.AreEqual(60, stored.TempbanMinutes);
            Assert.AreEqual("spambot", stored.InlineBots.Single().Value);
            Assert.AreEqual("badpack", stored.StickerSets.Single());
            Assert.IsTrue(stored.ChannelPosts.Enabled);
            Assert.IsTrue(stored.ChannelPosts.IsAllowed(-1009));

            foreach (var (hash, fields) in before)
                Assert.AreEqual(fields, string.Join(",", chat.Hashes.All($"chat:{id}:{hash}").OrderBy(f => f.Key).Select(f => $"{f.Key}={f.Value}")), hash);
            Assert.IsNotNull(chat.Keys[DetectionSettings.CacheKey(id)], "cached in Redis");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AMenuChange_IsSavedInPostgres_NotInTheOldHashes_AndTheCheckFollowsIt()
        {
            await using var chat = await HarnessChat.StartAsync();
            var id = chat.ChatId;
            chat.Hashes[$"chat:{id}:settings", "Flood"] = "yes"; // off
            chat.Hashes[$"chat:{id}:flood", "MaxFlood"] = "3";
            chat.Hashes[$"chat:{id}:flood", "ActionFlood"] = "kick";
            var admin = chat.Add("Admin", MemberState.Administrator);
            var flooder = chat.Add("Flooder");

            await chat.PressDataAsync(admin, Menu(chat, admin), $"floodstatus:{id}");

            Assert.IsTrue((await Stored(chat)).Flood.Enabled);
            Assert.AreEqual("yes", chat.Hashes[$"chat:{id}:settings", "Flood"], "the old hash is no longer written");
            for (var i = 0; i < 5; i++) await chat.SendAsync(flooder, $"spam {i}");
            Assert.AreEqual(MemberState.Left, State(chat, flooder), "kicked: flood is on");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AfterARestart_TheSavedSettingsStand_FromTheCache_OrFromPostgres()
        {
            await using var chat = await HarnessChat.StartAsync();
            var id = chat.ChatId;
            chat.Hashes[$"chat:{id}:char", "Arab"] = "allowed";
            var admin = chat.Add("Admin", MemberState.Administrator);
            var menu = Menu(chat, admin);
            // off -> delete -> warn -> mute -> kick -> tempban -> ban
            for (var i = 0; i < 6; i++) await chat.PressDataAsync(admin, menu, $"filterpreset:{id}:arabic");
            Assert.AreEqual("ban", (await Stored(chat)).Filters.PresetAction(FilterPresets.Arabic));

            await chat.CrashAndRestartAsync();
            var first = chat.Add("First");
            await chat.SendAsync(first, Arabic);
            Assert.AreEqual(MemberState.Kicked, State(chat, first), "from the cache, not the old hash");

            // Memory and cache both gone: PostgreSQL, not the old hash.
            await chat.CrashAndRestartAsync();
            chat.Keys[DetectionSettings.CacheKey(id)] = null;
            chat.Keys[DetectionSettings.VersionKey(id)] = null;
            var second = chat.Add("Second");
            await chat.SendAsync(second, Arabic);
            Assert.AreEqual(MemberState.Kicked, State(chat, second));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task WithoutPostgres_AMenuChangeIsRefused_AndTheChecksKeepTheirSettings()
        {
            await using var chat = await HarnessChat.StartAsync();
            var id = chat.ChatId;
            chat.Hashes[$"chat:{id}:char", "Arab"] = "ban";
            var admin = chat.Add("Admin", MemberState.Administrator);
            await chat.SendAsync(admin, "loaded");

            Enforcer5.Storage.PostgresStorage.UseForTests(null);
            try
            {
                await chat.PressDataAsync(admin, Menu(chat, admin), $"filterpreset:{id}:arabic");
                var failed = Enforcer5.Helpers.Methods.GetLocaleString(Enforcer5.Helpers.Methods.GetGroupLanguage(id).Doc, "failed");
                CollectionAssert.Contains(CallbackAnswers(chat), failed);

                var member = chat.Add("Member");
                await chat.SendAsync(member, Arabic);
                Assert.AreEqual(MemberState.Kicked, State(chat, member), "still ban, from memory");
            }
            finally
            {
                Enforcer5.Storage.PostgresStorage.UseForTests(chat.Database);
            }
            Assert.AreEqual("ban", (await Stored(chat)).Filters.PresetAction(FilterPresets.Arabic));
            Assert.IsTrue(chat.Errors.All(e => e.Contains("PostgreSQL is not ready")), string.Join("\n", chat.Errors));
        }

        [TestMethod]
        public async Task Blocklists_ChangedByCommand_AreSavedInPostgres()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);

            await chat.SendAsync(admin, "/blockinline @spambot");

            var stored = await Stored(chat);
            Assert.AreEqual("spambot", stored.InlineBots.Single().Value);
            Assert.AreEqual(0, chat.Keys.Members($"chat:{chat.ChatId}:blockedinline").Count, "not in Redis");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AGroupTheBotIsAddedTo_GetsTheNewGroupDefaults_InPostgres()
        {
            await using var chat = await HarnessChat.StartAsync();
            Enforcer5.Service.GenerateSettings(chat.ChatId);

            var stored = await Stored(chat);
            Assert.IsNotNull(stored.Engine.ObserveUntil, "its observe weeks start");
            Assert.IsTrue((stored with { Engine = EngineSettings.Default }).SameSettingsAs(DetectionPolicy.Empty.WithNewGroupDefaults()));
            Assert.IsNull(chat.Hashes[$"chat:{chat.ChatId}:settings", "Flood"], "no longer written to Redis");
            Assert.AreEqual("yes", chat.Hashes[$"chat:{chat.ChatId}:settings", "Rules"], "the rest still is");
            AssertNoErrors(chat);
        }
    }
}
