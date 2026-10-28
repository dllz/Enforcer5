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
    /// Detection step 5: the filters menu, the presets acting on real messages and joins, the two
    /// choices for all filters (established members, bots in the group) and /filter with its trial.
    /// Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Detection")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class FilterScenarios
    {
        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static MemberState State(HarnessChat chat, HarnessUser user) => chat.Telegram.GetMember(chat.ChatId, user.Id);

        private static Task<DetectionPolicy> Stored(HarnessChat chat) =>
            new PostgresDetectionSettingsRepository(() => chat.Database).LoadAsync(chat.ChatId);

        private static Message PrivateMenu(HarnessChat chat, HarnessUser admin) => new Message
        {
            Id = chat.Telegram.NextMessageId(),
            Chat = new Chat { Id = admin.Id, Type = ChatType.Private },
            From = HarnessChat.BotUser,
            Text = "Settings"
        };

        private static async Task SetPreset(HarnessChat chat, HarnessUser admin, string preset, string action)
        {
            var menu = PrivateMenu(chat, admin);
            for (var i = 0; i < Enforcer5.CallBacks.PresetActions.Length; i++)
            {
                if ((await Stored(chat))?.Filters.PresetAction(preset) == action) return;
                await chat.PressDataAsync(admin, menu, $"filterpreset:{chat.ChatId}:{preset}");
            }
            Assert.AreEqual(action, (await Stored(chat)).Filters.PresetAction(preset));
        }

        private static async Task<(string Sign, string Mode, string Action)[]> Detections(HarnessChat chat)
        {
            await chat.SettleAsync();
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<(string, string, string)>(
                "SELECT sign, mode, action FROM detections ORDER BY detection_id")).ToArray();
        }

        private static string[] SanctionReasons(HarnessChat chat)
        {
            using var connection = chat.Database.OpenConnectionAsync().GetAwaiter().GetResult();
            return connection.Query<string>("SELECT reason FROM sanctions ORDER BY sanction_id").ToArray();
        }

        [TestMethod]
        public async Task TheMenu_SetsAPreset_AndItDeletesTheMatchingMessage()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await chat.PressDataAsync(admin, PrivateMenu(chat, admin), $"filtersmenu:{chat.ChatId}");
            await SetPreset(chat, admin, "invite_link", "delete");
            var member = chat.Add("Member");

            await chat.SendAsync(member, "hello friends, come and join our club at t.me/+cheapfollowers today");
            await chat.SendAsync(member, "a normal message");

            Assert.AreEqual(1, chat.Telegram.MessagesFrom(chat.ChatId, member.Id).Count, "the invite went, the other stayed");
            Assert.AreEqual(MemberState.Member, State(chat, member));
            Assert.AreEqual(0, chat.Replies.Count, "a delete is silent: " + string.Join(" | ", chat.Replies));
            CollectionAssert.Contains(await Detections(chat), ("filter:invite_link", "live", "delete"));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ACaption_IsText_AndAJoinersNameIsChecked()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await SetPreset(chat, admin, "arabic", "kick");
            await SetPreset(chat, admin, "staff_words", "ban");
            var poster = chat.Add("Poster");
            var support = chat.Add("Telegram Support", MemberState.Left);
            var badminton = chat.Add("Badminton fan", MemberState.Left);

            await chat.PostAsync(chat.Photo(poster, caption: "سلام"));
            await chat.JoinAsync(support);
            await chat.JoinAsync(badminton);

            Assert.AreEqual(MemberState.Left, State(chat, poster), "kicked for the caption");
            Assert.AreEqual(MemberState.Kicked, State(chat, support));
            Assert.AreEqual(MemberState.Member, State(chat, badminton));
            CollectionAssert.AreEquivalent(new[] { "Non-Latin script", "Filter staff_words" }, SanctionReasons(chat));
            Assert.IsTrue(chat.Replies.Any(r => r.Contains("Telegram Support")), "the ban is announced");
            AssertNoErrors(chat);
        }

        private static Message ViaBot(HarnessChat chat, HarnessUser from, string text, long botId)
        {
            var message = chat.Text(from, text);
            message.ViaBot = new User { Id = botId, IsBot = true, FirstName = "Translator", Username = "TranslatorBot" };
            return message;
        }

        [TestMethod]
        public async Task BotsInTheGroup_MayAnswerInAnyScript_WhenTheGroupAllowsThem_ButNotBotsOutsideIt()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await SetPreset(chat, admin, "arabic", "kick");
            const long inGroup = 777001, outside = 777002;
            chat.Telegram.SetMember(chat.ChatId, inGroup, MemberState.Member);
            var first = chat.Add("First");
            var second = chat.Add("Second");
            var third = chat.Add("Third");

            await chat.PostAsync(ViaBot(chat, first, "سلام", inGroup));
            Assert.AreEqual(MemberState.Left, State(chat, first), "not allowed yet");

            await chat.PressDataAsync(admin, PrivateMenu(chat, admin), $"filtertoggle:{chat.ChatId}:bots");
            Assert.IsTrue((await Stored(chat)).Filters.ExemptGroupBots);
            await chat.PostAsync(ViaBot(chat, second, "سلام", inGroup));
            await chat.PostAsync(ViaBot(chat, third, "سلام", outside));

            Assert.AreEqual(MemberState.Member, State(chat, second), "a bot of the group");
            Assert.AreEqual(MemberState.Left, State(chat, third), "a bot from outside the group");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task EstablishedMembers_AreCheckedUnlessTheGroupSaysNot()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await SetPreset(chat, admin, "cyrillic", "kick");
            var regular = chat.Add("Regular");
            chat.Hashes[$"{chat.ChatId}:users:{regular.Id}", "msgs"] = "5000";
            var newcomer = chat.Add("Newcomer");

            await chat.PressDataAsync(admin, PrivateMenu(chat, admin), $"filtertoggle:{chat.ChatId}:established");
            Assert.IsFalse((await Stored(chat)).Filters.IncludeEstablished);
            await chat.SendAsync(regular, "Привет");
            await chat.SendAsync(newcomer, "Привет");

            Assert.AreEqual(MemberState.Member, State(chat, regular));
            Assert.AreEqual(MemberState.Left, State(chat, newcomer));

            await chat.PressDataAsync(admin, PrivateMenu(chat, admin), $"filtertoggle:{chat.ChatId}:established");
            await chat.SendAsync(regular, "Привет");
            Assert.AreEqual(MemberState.Left, State(chat, regular), "checked again");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnOwnRule_StartsOnTrial_RecordsOnly_AndActsOnceMadeLive()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var first = chat.Add("First");
            var second = chat.Add("Second");

            await chat.SendAsync(admin, "/filter add text ban free +crypto");
            var rule = (await Stored(chat)).Filters.OwnRules.Single();
            Assert.AreEqual((FilterRule.Trial, "text", "ban", admin.Id), (rule.Mode, rule.Target, rule.Action, rule.CreatedBy));
            StringAssert.Contains(chat.Replies.Last(), $"#{rule.Id}");

            await chat.SendAsync(first, "Get FREE crypto now");
            Assert.AreEqual(MemberState.Member, State(chat, first), "on trial");
            CollectionAssert.Contains(await Detections(chat), ($"filter:{rule.Id}", "trial", "ban"));

            await chat.SendAsync(admin, $"/filter live {rule.Id}");
            await chat.SendAsync(second, "free  crypto for all");
            Assert.AreEqual(MemberState.Kicked, State(chat, second));
            CollectionAssert.Contains(SanctionReasons(chat), $"Filter #{rule.Id}");

            await chat.SendAsync(admin, "/filter list");
            var list = chat.Replies.Last();
            StringAssert.Contains(list, $"#{rule.Id}");
            StringAssert.Contains(list, "free +crypto");

            await chat.SendAsync(admin, $"/filter del {rule.Id}");
            Assert.AreEqual(0, (await Stored(chat)).Filters.OwnRules.Count());
            AssertNoErrors(chat);
        }

        // The command limiter lets an admin send a few commands in a row; these are spaced out.
        private static async Task Command(HarnessChat chat, HarnessUser from, string text)
        {
            await chat.AdvanceAsync(TimeSpan.FromSeconds(20));
            await chat.SendAsync(from, text);
        }

        [TestMethod]
        public async Task Filter_RefusesBadRules_AndTriesOne()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var lang = Enforcer5.Helpers.Methods.GetGroupLanguage(chat.ChatId).Doc;
            string S(string key, params object[] args) => Enforcer5.Helpers.Methods.GetLocaleString(lang, key, args);

            await Command(chat, admin, "/filter add avatar ban x");
            StringAssert.StartsWith(chat.Replies.Last(), S("filterBadTarget", "x").Split(':')[0]);
            await Command(chat, admin, "/filter add text explode x");
            StringAssert.StartsWith(chat.Replies.Last(), S("filterBadAction", "x").Split(':')[0]);
            await Command(chat, admin, "/filter add text ban (?=x)x");
            StringAssert.StartsWith(chat.Replies.Last(), S("filterInvalid", "x").Split(':')[0]);
            Assert.AreEqual(0, (await Stored(chat)).Filters.OwnRules.Count());

            await Command(chat, admin, "/filter try /free +crypto/ Get FREE crypto");
            Assert.AreEqual(S("filterTryMatch"), chat.Replies.Last());
            await Command(chat, admin, "/filter try /free +crypto/ freedom");
            Assert.AreEqual(S("filterTryNoMatch"), chat.Replies.Last());

            await Command(chat, admin, "/filter try spam spammer here");
            Assert.AreEqual(S("filterTryMatch"), chat.Replies.Last(), "one word needs no slashes");

            await Command(chat, member, "/filter add text ban x");
            Assert.AreEqual(0, (await Stored(chat)).Filters.OwnRules.Count(), "admins only");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task FilterTest_TriesTheRuleOnRecentMembers_AndSendsTheNamesInPrivate()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var support = chat.Add("Crypto Support");
            var anna = chat.Add("Anna");
            await chat.SendAsync(support, "hello");
            await chat.SendAsync(anna, "hello");
            await chat.SettleAsync();

            await chat.SendAsync(admin, "/filter test support");

            var result = chat.Telegram.SentMessages.Last(m => m.Chat.Id == admin.Id).Text;
            StringAssert.Contains(result, "Crypto Support");
            Assert.IsFalse(result.Contains("Anna"), result);
            Assert.IsFalse(chat.Replies.Any(r => r.Contains("Crypto Support")), "no names in the group");
            AssertNoErrors(chat);
        }
    }
}
