using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Warn removal reasons in the group's config in PostgreSQL (rollout step 17): the /menu mode, /addwarnreason and
    /// /delwarnreason, the recent reasons, each group's old Redis keys imported with its config. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class WarnReasonScenarios
    {
        private sealed class Row
        {
            public short Mode { get; set; }
            public string[] Saved { get; set; }
            public string[] Recent { get; set; }
        }

        private static async Task<Row> Stored(HarnessChat chat, long? chatId = null)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return await connection.QuerySingleAsync<Row>(@"
SELECT s.warn_reason_mode AS Mode, s.warn_reasons AS Saved, s.recent_warn_reasons AS Recent
FROM group_settings s JOIN groups g ON g.group_id = s.group_id JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE c.telegram_chat_id = @Chat", new { Chat = chatId ?? chat.ChatId });
        }

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        [TestMethod]
        public async Task TheMode_SavedAndRecentReasons_AreInPostgres_AndNotInRedis()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");

            await chat.SendAsync(admin, "/addwarnreason Spam in links");
            await chat.PressDataAsync(admin, chat.LastBotMessage, $"menuWarnReasons:{chat.ChatId}");
            await chat.SendAsync(admin, "/delwarnreason Wrong user");
            await chat.AdvanceAsync(TimeSpan.FromMinutes(1)); // under the command limit
            var warned = await chat.SendAsync(member, "hello");
            await chat.SendAsync(admin, "/warn", replyTo: warned);
            await chat.SendAsync(admin, "/removewarn AFK", replyTo: warned);

            var row = await Stored(chat);
            Assert.AreEqual(1, row.Mode, "Off to Warns");
            CollectionAssert.AreEqual(new[] { "Mistake", "Appeal accepted", "Warn served its time", "Spam in links" }, row.Saved);
            CollectionAssert.AreEqual(new[] { "AFK" }, row.Recent);
            Assert.IsFalse(chat.Keys.Keys.Any(k => k.Contains(":warnreasonmode") || k.EndsWith(":warnreasons") || k.Contains(":recentwarnreasons")),
                "the old keys are not written any more");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task OldKeys_AreImportedWithTheGroupsConfig()
        {
            await using var chat = await HarnessChat.StartAsync();
            var other = chat.Group(-1002224446);
            chat.Keys[$"chat:{other.ChatId}:warnreasonmode"] = "2";
            chat.Keys[$"chat:{other.ChatId}:warnreasons"] = "[\"Mistake\",\"Old reason\"]";

            var config = await GroupConfigs.GetAsync(other.ChatId);
            await chat.SettleAsync();

            Assert.AreEqual(Enforcer5.Models.WarnReasonMode.WarnsAndPrewarns, config.WarnReasonMode);
            var row = await Stored(chat, other.ChatId);
            Assert.AreEqual(2, row.Mode);
            CollectionAssert.AreEqual(new[] { "Mistake", "Old reason" }, row.Saved);
            Assert.AreEqual("2", chat.Keys[$"chat:{other.ChatId}:warnreasonmode"], "the old keys stay, for a rollback");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task WhilePostgresIsDown_AChangeCountsAtOnce_AndIsWrittenOnceItIsBack()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await chat.SendAsync(admin, "/addwarnreason First");
            var database = chat.Database;
            Enforcer5.Storage.PostgresStorage.UseForTests(null);

            await chat.SendAsync(admin, "/addwarnreason Second");
            await chat.SendAsync(admin, "/warnreasons");
            StringAssert.Contains(chat.Replies.Last(), "Second", "counts in memory at once");

            Enforcer5.Storage.PostgresStorage.UseForTests(database);
            await GroupConfigs.DrainAsync();

            CollectionAssert.AreEqual(new[] { "Mistake", "Appeal accepted", "Wrong user", "Warn served its time", "First", "Second" },
                (await Stored(chat)).Saved);
        }
    }
}
