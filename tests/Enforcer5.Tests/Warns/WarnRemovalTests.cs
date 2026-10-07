using System;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StackExchange.Redis;

namespace Enforcer5.Tests.Warns
{
    [TestClass]
    public class WarnReasonRuleTests
    {
        [DataTestMethod]
        [DataRow((int)WarnReasonMode.Off, false, false, false)]
        [DataRow((int)WarnReasonMode.Warns, true, false, false)]
        [DataRow((int)WarnReasonMode.WarnsAndPrewarns, true, true, false)]
        [DataRow((int)WarnReasonMode.All, true, true, true)]
        public void Mode_DecidesWhichKindsNeedAReason(int level, bool warn, bool prewarn, bool media)
        {
            var mode = (WarnReasonMode)level;
            Assert.AreEqual(warn, mode.Requires(WarnKind.Warn));
            Assert.AreEqual(prewarn, mode.Requires(WarnKind.Prewarn));
            Assert.AreEqual(media, mode.Requires(WarnKind.Media));
        }

        [TestMethod]
        public void Mode_CyclesThroughEveryLevel()
        {
            Assert.AreEqual(WarnReasonMode.Warns, WarnReasonMode.Off.Next());
            Assert.AreEqual(WarnReasonMode.WarnsAndPrewarns, WarnReasonMode.Warns.Next());
            Assert.AreEqual(WarnReasonMode.All, WarnReasonMode.WarnsAndPrewarns.Next());
            Assert.AreEqual(WarnReasonMode.Off, WarnReasonMode.All.Next());
            Assert.AreEqual(WarnReasonMode.Off, ((WarnReasonMode)42).Next());
        }

        [TestMethod]
        public void Mode_NumbersAreStable()
        {
            // Stored as the number; changing these needs a data migration.
            CollectionAssert.AreEqual(new[] { 0, 1, 2, 3 },
                new[] { WarnReasonMode.Off, WarnReasonMode.Warns, WarnReasonMode.WarnsAndPrewarns, WarnReasonMode.All }
                    .Select(m => (int)m).ToArray());
        }

        [DataTestMethod]
        [DataRow("  Appeal   accepted ", "Appeal accepted")]
        [DataRow("AFK\nwarn", "AFK warn")]
        [DataRow("x", "x")]
        public void Reason_IsTrimmedAndCollapsed(string input, string expected)
        {
            Assert.AreEqual(expected, WarnReasons.Normalise(input));
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("   ")]
        public void EmptyReason_IsRejected(string input)
        {
            Assert.IsNull(WarnReasons.Normalise(input));
        }

        [TestMethod]
        public void ReasonLength_IsCapped()
        {
            Assert.IsNotNull(WarnReasons.Normalise(new string('a', WarnReasons.MaxLength)));
            Assert.IsNull(WarnReasons.Normalise(new string('a', WarnReasons.MaxLength + 1)));
        }

        [TestMethod]
        public void Choices_PutSavedFirstThenRecentWithoutDuplicates()
        {
            var choices = WarnReasons.Choices(new[] { "Mistake", "AFK" }, new[] { "afk", "Spam", "", "Mistake" });
            CollectionAssert.AreEqual(new[] { "Mistake", "AFK", "Spam" }, choices);
        }

        [TestMethod]
        public void Choices_AreCapped()
        {
            var saved = Enumerable.Range(1, 10).Select(i => $"saved {i}");
            var choices = WarnReasons.Choices(saved, new[] { "recent" });
            Assert.AreEqual(WarnReasons.MaxChoices, choices.Length);
            Assert.AreEqual("saved 1", choices[0]);
            Assert.IsFalse(choices.Contains("recent"));
        }

        [TestMethod]
        public void Choices_HandleMissingLists()
        {
            Assert.AreEqual(0, WarnReasons.Choices(null, null).Length);
            CollectionAssert.AreEqual(new[] { "x" }, WarnReasons.Choices(null, new[] { "x" }));
        }

        [TestMethod]
        public void Defaults_AreParsedFromTheLanguageFile()
        {
            var english = XDocument.Load(TestEnvironment.EnglishXmlPath);
            var defaults = WarnReasons.ParseDefaults(Methods.GetLocaleString(english, "warnReasonDefaults"));
            CollectionAssert.AreEqual(new[] { "Mistake", "Appeal accepted", "Wrong user", "Warn served its time" }, defaults.ToArray());
            CollectionAssert.AreEqual(new[] { "a", "b" }, WarnReasons.ParseDefaults(" a || b |").ToArray());
            Assert.AreEqual(0, WarnReasons.ParseDefaults(null).Count);
        }

        [DataTestMethod]
        [DataRow(42L, 42L, false, (int)Answerer.Allowed)]                     // the admin who asked
        [DataRow(42L, 7L, false, (int)Answerer.Refused)]                      // a different admin
        [DataRow(42L, WarnReasons.AnonymousAdminId, true, (int)Answerer.Allowed)] // anonymous admins cannot be told apart
        [DataRow(WarnReasons.AnonymousAdminId, 7L, false, (int)Answerer.AllowedIfAdmin)]
        [DataRow(WarnReasons.AnonymousAdminId, WarnReasons.AnonymousAdminId, true, (int)Answerer.Allowed)]
        public void OnlyTheAskingAdminMayAnswer(long adminId, long responderId, bool anonymous, int expected)
        {
            Assert.AreEqual((Answerer)expected, WarnReasons.WhoMayAnswer(adminId, responderId, anonymous));
        }

        [DataTestMethod]
        [DataRow("mistake", 0)]
        [DataRow("Wrong user", 1)]
        [DataRow("2", 1)]
        [DataRow("3", 2)]
        [DataRow("0", -1)]
        [DataRow("4", -1)]
        [DataRow("nope", -1)]
        public void Reason_IsFoundByTextOrNumber(string input, int expected)
        {
            Assert.AreEqual(expected, Commands.FindWarnReason(new[] { "Mistake", "Wrong user", "AFK" }, input));
        }

        [TestMethod]
        public void NumericReason_MatchesItsTextBeforeItsPosition()
        {
            Assert.AreEqual(1, Commands.FindWarnReason(new[] { "Mistake", "1" }, "1"));
        }
    }

    [TestClass]
    public class RedisWarnRepositoryTests
    {
        private const long ChatId = -1001234567890;
        private FakeRedis _redis;
        private IWarnRepository _warns;

        [TestInitialize]
        public void SetUp()
        {
            _redis = new FakeRedis();
            _warns = new RedisWarnRepository(() => _redis.Database);
        }

        [DataTestMethod]
        [DataRow((int)WarnKind.Warn, "chat:-1001234567890:warns")]
        [DataRow((int)WarnKind.Prewarn, "chat:-1001234567890:prewarns")]
        [DataRow((int)WarnKind.Media, "chat:-1001234567890:mediawarn")]
        public async Task EachKind_UsesItsExistingHash(int kindValue, string key)
        {
            var kind = (WarnKind)kindValue;
            _redis.Hash(key)[42] = 3;
            Assert.AreEqual(3L, await _warns.GetCountAsync(ChatId, kind, 42));
            Assert.AreEqual(2L, await _warns.RemoveOneAsync(ChatId, kind, 42));
            Assert.AreEqual((RedisValue)2, _redis.Hash(key)[42]);
            await _warns.ResetAsync(ChatId, kind, 42);
            Assert.IsFalse(_redis.Hash(key).ContainsKey(42));
        }

        [TestMethod]
        public async Task MissingOrNegativeCount_ReadsAsZero()
        {
            Assert.AreEqual(0L, await _warns.GetCountAsync(ChatId, WarnKind.Warn, 42));
            _redis.Hash("chat:-1001234567890:warns")[42] = -2;
            Assert.AreEqual(0L, await _warns.GetCountAsync(ChatId, WarnKind.Warn, 42));
        }

        [TestMethod]
        public async Task RemovingFromZero_StaysAtZero()
        {
            // A negative count would silently cancel out the user's next warn.
            Assert.AreEqual(0L, await _warns.RemoveOneAsync(ChatId, WarnKind.Warn, 42));
            Assert.AreEqual((RedisValue)0, _redis.Hash("chat:-1001234567890:warns")[42]);
        }

        [TestMethod]
        public void Constructing_DoesNotTouchTheDatabase()
        {
            _ = new RedisWarnRepository(() => throw new InvalidOperationException("not connected"));
            Assert.IsInstanceOfType(Repositories.Warns, typeof(RedisWarnRepository));
        }
    }

    [TestClass]
    public class RedisWarnReasonRepositoryTests
    {
        private const long ChatId = -1001234567890;
        private FakeRedis _redis;
        private IWarnReasonRepository _reasons;

        [TestInitialize]
        public void SetUp()
        {
            _redis = new FakeRedis();
            _reasons = new RedisWarnReasonRepository(() => _redis.Database);
        }

        private static PendingWarnRemoval Pending() => new PendingWarnRemoval(
            new WarnChange(WarnKind.Warn, true, 42, AlsoMediaWarns: true), 7, "Admin <b>", 99, true, new[] { "Mistake", "AFK" });

        [TestMethod]
        public async Task Mode_DefaultsToOffAndRoundTrips()
        {
            Assert.AreEqual(WarnReasonMode.Off, await _reasons.GetModeAsync(ChatId));
            await _reasons.SetModeAsync(ChatId, WarnReasonMode.WarnsAndPrewarns);
            Assert.AreEqual(WarnReasonMode.WarnsAndPrewarns, await _reasons.GetModeAsync(ChatId));
        }

        [DataTestMethod]
        [DataRow("9")]
        [DataRow("-1")]
        [DataRow("on")]
        public async Task UnknownMode_ReadsAsOff(string stored)
        {
            _redis.Strings["chat:-1001234567890:warnreasonmode"] = (stored, null);
            Assert.AreEqual(WarnReasonMode.Off, await _reasons.GetModeAsync(ChatId));
        }

        [TestMethod]
        public async Task SavedReasons_AreNullUntilEdited_AndAnEmptiedListStaysEmpty()
        {
            Assert.IsNull(await _reasons.GetSavedAsync(ChatId), "never edited: the defaults apply");

            await _reasons.SetSavedAsync(ChatId, new[] { "Mistake", "AFK \"warn\"" });
            CollectionAssert.AreEqual(new[] { "Mistake", "AFK \"warn\"" }, (await _reasons.GetSavedAsync(ChatId)).ToArray());

            await _reasons.SetSavedAsync(ChatId, Array.Empty<string>());
            var emptied = await _reasons.GetSavedAsync(ChatId);
            Assert.IsNotNull(emptied, "deliberately emptied: the defaults must not come back");
            Assert.AreEqual(0, emptied.Count);
        }

        [TestMethod]
        public async Task UnreadableSavedReasons_FallBackToTheDefaults()
        {
            _redis.Strings["chat:-1001234567890:warnreasons"] = ("not json", null);
            Assert.IsNull(await _reasons.GetSavedAsync(ChatId));
        }

        [TestMethod]
        public async Task RecentReasons_AreNewestFirstDistinctAndBounded()
        {
            foreach (var reason in new[] { "a", "b", "a", "c", "d", "e", "f" })
                await _reasons.AddRecentAsync(ChatId, reason);

            CollectionAssert.AreEqual(new[] { "f", "e", "d", "c", "a" }, (await _reasons.GetRecentAsync(ChatId)).ToArray());
        }

        [TestMethod]
        public async Task Pending_RoundTripsWithItsLifetime()
        {
            await _reasons.SavePendingAsync(ChatId, 500, Pending(), TimeSpan.FromMinutes(10));

            var stored = await _reasons.GetPendingAsync(ChatId, 500);
            Assert.AreEqual(Pending().Change, stored.Change);
            Assert.AreEqual(7L, stored.AdminId);
            Assert.AreEqual("Admin <b>", stored.AdminName);
            Assert.AreEqual(99, stored.SourceMessageId);
            Assert.IsTrue(stored.SourceIsBotMessage);
            CollectionAssert.AreEqual(new[] { "Mistake", "AFK" }, stored.Reasons);
            Assert.AreEqual(TimeSpan.FromMinutes(10), _redis.Strings["chat:-1001234567890:warnreasonprompt:500"].Ttl);
            Assert.IsNull(await _reasons.GetPendingAsync(ChatId, 501));
        }

        [TestMethod]
        public async Task Pending_CanBeClaimedOnlyOnce()
        {
            await _reasons.SavePendingAsync(ChatId, 500, Pending(), TimeSpan.FromMinutes(10));
            Assert.IsTrue(await _reasons.ClaimPendingAsync(ChatId, 500));
            Assert.IsFalse(await _reasons.ClaimPendingAsync(ChatId, 500), "a second answer must not apply it again");
            Assert.IsNull(await _reasons.GetPendingAsync(ChatId, 500));
        }

        [TestMethod]
        public async Task UnreadablePending_IsTreatedAsExpired()
        {
            _redis.Strings["chat:-1001234567890:warnreasonprompt:500"] = ("{", null);
            Assert.IsNull(await _reasons.GetPendingAsync(ChatId, 500));
        }

        [TestMethod]
        public async Task StorageFormat_IsStable()
        {
            // Existing data depends on this layout; changing it needs a migration.
            await _reasons.SetModeAsync(ChatId, WarnReasonMode.All);
            await _reasons.SetSavedAsync(ChatId, new[] { "Mistake" });
            await _reasons.AddRecentAsync(ChatId, "AFK");
            await _reasons.SavePendingAsync(ChatId, 500, Pending(), TimeSpan.FromMinutes(10));

            CollectionAssert.AreEquivalent(new[]
            {
                "chat:-1001234567890:warnreasonmode",
                "chat:-1001234567890:warnreasons",
                "chat:-1001234567890:recentwarnreasons",
                "chat:-1001234567890:warnreasonprompt:500"
            }, _redis.Keys.ToArray());
            Assert.AreEqual("3", _redis.Strings["chat:-1001234567890:warnreasonmode"].Value.ToString());
            Assert.AreEqual("[\"Mistake\"]", _redis.Strings["chat:-1001234567890:warnreasons"].Value.ToString());
        }

        [TestMethod]
        public void Constructing_DoesNotTouchTheDatabase()
        {
            _ = new RedisWarnReasonRepository(() => throw new InvalidOperationException("not connected"));
            Assert.IsInstanceOfType(Repositories.WarnReasons, typeof(RedisWarnReasonRepository));
        }
    }

    [TestClass]
    public class WarnRemovalFlowTests
    {
        private const long ChatId = -1001234567890;
        private const string Warns = "chat:-1001234567890:warns";
        private const string Media = "chat:-1001234567890:mediawarn";
        private static XDocument _english;
        private FakeRedis _redis;
        private IWarnRepository _warns;
        private IWarnReasonRepository _reasons;

        [ClassInitialize]
        public static void Load(TestContext context)
        {
            _english = XDocument.Load(TestEnvironment.EnglishXmlPath);
        }

        [TestInitialize]
        public void SetUp()
        {
            _redis = new FakeRedis();
            _warns = new RedisWarnRepository(() => _redis.Database);
            _reasons = new RedisWarnReasonRepository(() => _redis.Database);
        }

        [TestMethod]
        public async Task RemoveOne_LeavesTheRestAndRemembersTheReason()
        {
            _redis.Hash(Warns)[42] = 2;
            var left = await Enforcer5.WarnRemovals.ApplyAsync(_warns, _reasons, ChatId, new WarnChange(WarnKind.Warn, false, 42), "AFK");
            Assert.AreEqual(1L, left);
            CollectionAssert.AreEqual(new[] { "AFK" }, (await _reasons.GetRecentAsync(ChatId)).ToArray());
        }

        [TestMethod]
        public async Task NoReason_IsNotRemembered()
        {
            _redis.Hash(Warns)[42] = 2;
            await Enforcer5.WarnRemovals.ApplyAsync(_warns, _reasons, ChatId, new WarnChange(WarnKind.Warn, false, 42), null);
            Assert.AreEqual(0, (await _reasons.GetRecentAsync(ChatId)).Count);
        }

        [TestMethod]
        public async Task ResetFromTheWarnMessage_AlsoClearsMediaWarns()
        {
            _redis.Hash(Warns)[42] = 2;
            _redis.Hash(Media)[42] = 1;
            var change = new WarnChange(WarnKind.Warn, true, 42, AlsoMediaWarns: true);

            Assert.AreEqual(3L, await Enforcer5.WarnRemovals.CountAsync(_warns, ChatId, change));
            Assert.AreEqual(0L, await Enforcer5.WarnRemovals.ApplyAsync(_warns, _reasons, ChatId, change, null));
            Assert.IsFalse(_redis.Hash(Warns).ContainsKey(42));
            Assert.IsFalse(_redis.Hash(Media).ContainsKey(42));
        }

        [TestMethod]
        public async Task ResetFromTheUserMenu_LeavesMediaWarns()
        {
            _redis.Hash(Warns)[42] = 2;
            _redis.Hash(Media)[42] = 1;
            var change = new WarnChange(WarnKind.Warn, true, 42);

            Assert.AreEqual(2L, await Enforcer5.WarnRemovals.CountAsync(_warns, ChatId, change));
            await Enforcer5.WarnRemovals.ApplyAsync(_warns, _reasons, ChatId, change, null);
            Assert.AreEqual((RedisValue)1, _redis.Hash(Media)[42]);
        }

        [TestMethod]
        public async Task NothingToRemove_CountsAsZero()
        {
            // Request stops there: no question, no log entry.
            Assert.AreEqual(0L, await Enforcer5.WarnRemovals.CountAsync(_warns, ChatId, new WarnChange(WarnKind.Prewarn, false, 42)));
        }

        [TestMethod]
        public async Task SavedReasons_FallBackToTheDefaultsUntilEdited()
        {
            CollectionAssert.AreEqual(new[] { "Mistake", "Appeal accepted", "Wrong user", "Warn served its time" },
                (await Enforcer5.WarnRemovals.SavedOrDefaultsAsync(_reasons, ChatId, _english)).ToArray());

            await _reasons.SetSavedAsync(ChatId, Array.Empty<string>());
            Assert.AreEqual(0, (await Enforcer5.WarnRemovals.SavedOrDefaultsAsync(_reasons, ChatId, _english)).Count);
        }

        [TestMethod]
        public void GroupMessage_ShowsTheReasonOnlyWhenThereIsOne()
        {
            var change = new WarnChange(WarnKind.Warn, false, 42);
            var withReason = Enforcer5.WarnRemovals.ResultText(_english, change, "Admin", "Player (42)", 1, "AFK");
            Assert.AreEqual(string.Join("\n",
                Methods.GetLocaleString(_english, "warnChangeRemoved", "Admin", "Player (42)"),
                "Warns: 1",
                Methods.GetLocaleString(_english, "warnChangeReason", "AFK")), withReason);

            var without = Enforcer5.WarnRemovals.ResultText(_english, change, "Admin", "Player (42)", 1, null);
            Assert.IsFalse(without.Contains("Reason"), without);
        }

        [TestMethod]
        public void LogEntry_NamesAdminTargetGroupAndReason()
        {
            var change = new WarnChange(WarnKind.Prewarn, true, 42);
            var text = Enforcer5.WarnRemovals.LogText(_english, change, "Admin", 7, "Player (42)", "Wolves", ChatId, 0, "Mistake");
            StringAssert.Contains(text, "Admin (7)");
            StringAssert.Contains(text, "Player (42)");
            StringAssert.Contains(text, $"Wolves ({ChatId})");
            StringAssert.Contains(text, "Pre-warns: 0");
            StringAssert.Contains(text, "Mistake");

            var noReason = Enforcer5.WarnRemovals.LogText(_english, change, "Admin", 7, "Player (42)", "", ChatId, 0, null);
            StringAssert.Contains(noReason, Methods.GetLocaleString(_english, "warnChangeNoReason"));
            StringAssert.Contains(noReason, $"in {ChatId}.");
        }

        [TestMethod]
        public void Texts_EscapeNamesAndReasonsForHtml()
        {
            // Sent as HTML; an unescaped < would make Telegram reject the message.
            var change = new WarnChange(WarnKind.Warn, false, 42);
            var text = Enforcer5.WarnRemovals.ResultText(_english, change, "<Admin>", "A&B", 0, "<i>afk</i>");
            StringAssert.Contains(text, "&lt;Admin&gt;");
            StringAssert.Contains(text, "A&amp;B");
            StringAssert.Contains(text, "&lt;i&gt;afk&lt;/i&gt;");
        }

        [TestMethod]
        public void Prompt_SaysWhatIsBeingChangedAndTheCurrentCount()
        {
            var remove = Enforcer5.WarnRemovals.PromptText(_english, new WarnChange(WarnKind.Media, false, 42), "Admin", "Player", 2);
            StringAssert.Contains(remove, Methods.GetLocaleString(_english, "warnReasonPromptRemove", "Admin", "Player"));
            StringAssert.Contains(remove, "Media warns: 2");
            StringAssert.Contains(remove, Methods.GetLocaleString(_english, "warnReasonPromptHint"));

            var reset = Enforcer5.WarnRemovals.PromptText(_english, new WarnChange(WarnKind.Warn, true, 42), "Admin", "Player", 2);
            StringAssert.Contains(reset, Methods.GetLocaleString(_english, "warnReasonPromptReset", "Admin", "Player"));
        }

        [TestMethod]
        public void EveryMode_HasAMenuLabel()
        {
            // The menu builds this key from the enum name, so the source scan cannot check it.
            foreach (WarnReasonMode mode in Enum.GetValues(typeof(WarnReasonMode)))
                Assert.IsFalse(string.IsNullOrEmpty(Methods.GetLocaleString(_english, $"warnReasonMode{mode}")), mode.ToString());
        }

        [TestMethod]
        public void ReasonReply_IsOnlyConsideredForTextRepliesToTheBot()
        {
            const long botId = 1000;
            Telegram.Bot.Types.Message Reply(long toId, string text) => new Telegram.Bot.Types.Message
            {
                Text = text,
                ReplyToMessage = new Telegram.Bot.Types.Message { From = new Telegram.Bot.Types.User { Id = toId } }
            };

            Assert.IsTrue(Enforcer5.WarnRemovals.MayBeReasonReply(Reply(botId, "AFK"), botId));
            Assert.IsFalse(Enforcer5.WarnRemovals.MayBeReasonReply(Reply(5, "AFK"), botId), "a reply to someone else");
            Assert.IsFalse(Enforcer5.WarnRemovals.MayBeReasonReply(Reply(botId, "/warn"), botId), "a command");
            Assert.IsFalse(Enforcer5.WarnRemovals.MayBeReasonReply(Reply(botId, null), botId), "not text");
            Assert.IsFalse(Enforcer5.WarnRemovals.MayBeReasonReply(new Telegram.Bot.Types.Message { Text = "AFK" }, botId), "not a reply");
        }

        [TestMethod]
        public void Callback_FitsTelegramsDataLimit()
        {
            // Buttons carry an index, never the reason text, so the longest data is the cancel one.
            var data = $"{Enforcer5.WarnRemovals.ReasonCallback}:{long.MinValue}:cancel";
            Assert.IsTrue(System.Text.Encoding.UTF8.GetByteCount(data) <= 64, data);
        }
    }
}
