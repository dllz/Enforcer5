using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using StackExchange.Redis;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Names, titles and last-seen times reach PostgreSQL from ordinary traffic (rollout step 3).
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class PrincipalScenarios
    {
        [TestMethod]
        public async Task ABan_InAGroup_StoresWhoAndWhereWithNames()
        {
            await using var chat = await HarnessChat.StartAsync(title: "Werewolf NL", username: "werewolfnl");
            chat.Redis.Setup(r => r.HashLengthAsync(It.Is<RedisKey>(k => k == $"chat:{chat.ChatId}:userlast"), It.IsAny<StackExchange.Redis.CommandFlags>()))
                .ReturnsAsync(42);
            var admin = chat.Add("Daniel", MemberState.Administrator, username: "daniel", languageCode: "en");
            var spammer = chat.Add("Spam", lastName: "Bot", username: "spammy");
            var spam = await chat.SendAsync(spammer, "buy now");
            await chat.SendAsync(admin, "/ban spam", replyTo: spam);

            await using var connection = await chat.Database.OpenConnectionAsync();
            var group = await connection.QuerySingleAsync(@"
SELECT g.title, g.username, g.public_link, g.active, g.observed_user_count, g.last_message_at IS NOT NULL AS has_last,
       (SELECT count(*) FROM telegram_chat_profiles) AS profiles
FROM groups g");
            Assert.AreEqual("Werewolf NL", (string)group.title);
            Assert.AreEqual("werewolfnl", (string)group.username);
            Assert.AreEqual("https://t.me/werewolfnl", (string)group.public_link);
            Assert.IsTrue((bool)group.active);
            Assert.AreEqual(42L, (long)group.observed_user_count);
            Assert.IsTrue((bool)group.has_last);
            Assert.AreEqual(1L, (long)group.profiles);

            var names = (await connection.QueryAsync<string>(@"
SELECT a.telegram_user_id || ' ' || p.display_name || ' @' || coalesce(p.username, '-') || ' ' || coalesce(p.language_code, '-')
FROM telegram_account_profiles p JOIN telegram_accounts a USING (account_id) WHERE p.valid_to IS NULL
ORDER BY a.telegram_user_id")).ToArray();
            CollectionAssert.AreEquivalent(new[]
            {
                $"{admin.Id} Daniel @daniel en",
                $"{spammer.Id} Spam Bot @spammy -"
            }, names);

            // The ban row and the observed profiles share one account and one user per person.
            Assert.AreEqual(2L, await connection.QuerySingleAsync<long>("SELECT count(*) FROM users"));
            Assert.AreEqual(2L, await connection.QuerySingleAsync<long>("SELECT count(*) FROM telegram_accounts"));
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));
        }

        [TestMethod]
        public async Task ARename_IsKeptAsHistory()
        {
            await using var chat = await HarnessChat.StartAsync(title: "Old name");
            var member = chat.Add("Ann");
            await chat.SendAsync(member, "hello");
            chat.Title = "New name";
            await chat.SendAsync(member, "hello again");

            await using var connection = await chat.Database.OpenConnectionAsync();
            var titles = (await connection.QueryAsync<string>(
                "SELECT title || CASE WHEN valid_to IS NULL THEN ' (current)' ELSE '' END FROM telegram_chat_profiles ORDER BY valid_from")).ToArray();
            CollectionAssert.AreEqual(new[] { "Old name", "New name (current)" }, titles);
            Assert.AreEqual("New name", await connection.QuerySingleAsync<string>("SELECT title FROM groups"));
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));
        }
    }
}
