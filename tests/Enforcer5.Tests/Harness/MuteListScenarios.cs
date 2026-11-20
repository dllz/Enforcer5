using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// The bot's mute lists in PostgreSQL (rollout step 16): /mute, /tempmute, Mute On Join, /mutelist,
    /// /listmutedjoiners and /unmutenewjoiners, each group's old Redis keys imported on its first use, and an upgraded
    /// group keeping them. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class MuteListScenarios
    {
        private static async Task<(long User, string Kind, bool HasEnd)[]> Rows(HarnessChat chat, long? chatId = null)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<(long, string, bool)>(@"
SELECT telegram_user_id, kind, until_at IS NOT NULL FROM group_mutes WHERE telegram_chat_id = @Chat ORDER BY telegram_user_id",
                new { Chat = chatId ?? chat.ChatId })).ToArray();
        }

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        [TestMethod]
        public async Task Mutes_AreListed_FromPostgres_AndNothingIsWrittenToTheOldKeys()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var muted = chat.Add("Muted");
            var tempmuted = chat.Add("Tempmuted");
            var freed = chat.Add("Freed");
            await chat.SendAsync(admin, "/mute", replyTo: await chat.SendAsync(muted, "a"));
            await chat.SendAsync(admin, "/tempmute 2h", replyTo: await chat.SendAsync(tempmuted, "b"));
            var freedMessage = await chat.SendAsync(freed, "c");
            await chat.SendAsync(admin, "/mute", replyTo: freedMessage);
            await chat.SendAsync(admin, "/unmute", replyTo: freedMessage);

            CollectionAssert.AreEqual(new[] { (muted.Id, "mute", false), (tempmuted.Id, "tempmute", true) }, await Rows(chat));

            await chat.SendAsync(admin, "/mutelist");
            var list = chat.Replies.Last();
            StringAssert.Contains(list, muted.Id.ToString());
            StringAssert.Contains(list, tempmuted.Id.ToString());
            Assert.IsFalse(list.Contains(freed.Id.ToString()), list);
            Assert.IsFalse(chat.Keys.Keys.Any(k => k.EndsWith(":muted") || k.EndsWith(":tempmuted") || k.EndsWith(":mutedJoiners")),
                "the old keys are not written any more");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task MuteOnJoin_IsListed_AndUnmuteNewJoinersLiftsOnlyThose()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:settings", "MuteOnJoin"] = "no";
            var admin = chat.Add("Admin", MemberState.Administrator);
            var joiner = new HarnessUser(424242, "Joiner");
            var comeback = new HarnessUser(434343, "Comeback");
            await chat.JoinAsync(joiner);
            // Muted for good by an admin, then left and came back: Mute On Join leaves that mute alone.
            await chat.JoinAsync(comeback);
            await chat.SendAsync(admin, "/mute " + comeback.Id);
            chat.Telegram.SetMember(chat.ChatId, comeback.Id, MemberState.Left);
            await chat.JoinAsync(comeback);
            CollectionAssert.AreEqual(new[] { (joiner.Id, "joiner", false), (comeback.Id, "mute", false) }, await Rows(chat));

            await chat.SendAsync(admin, "/listmutedjoiners");
            StringAssert.Contains(chat.Replies.Last(), joiner.Id.ToString());
            Assert.IsFalse(chat.Replies.Last().Contains(comeback.Id.ToString()));

            await chat.SendAsync(admin, "/unmutenewjoiners");

            Assert.AreNotEqual(MemberState.Restricted, chat.Telegram.GetMember(chat.ChatId, joiner.Id));
            CollectionAssert.AreEqual(new[] { (comeback.Id, "mute", false) }, await Rows(chat));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task OldRedisKeys_AreImportedOnTheGroupsFirstUse()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var old = chat.Add("Old", MemberState.Restricted);
            var oldJoiner = chat.Add("OldJoiner", MemberState.Restricted);
            var unmutedInTheApp = chat.Add("UnmutedInTheApp");
            chat.Keys.AddToSet($"chat:{chat.ChatId}:muted", old.Id.ToString());
            chat.Keys.AddToSet($"chat:{chat.ChatId}:muted", unmutedInTheApp.Id.ToString());
            chat.Keys.AddToSet($"chat:{chat.ChatId}:mutedJoiners", oldJoiner.Id.ToString());

            await chat.SendAsync(admin, "/mutelist");

            StringAssert.Contains(chat.Replies.Last(), old.Id.ToString());
            Assert.IsFalse(chat.Replies.Last().Contains(unmutedInTheApp.Id.ToString()), "Telegram shows them free: dropped");
            CollectionAssert.AreEqual(new[] { (old.Id, "mute", false), (oldJoiner.Id, "joiner", false) }, await Rows(chat));
            CollectionAssert.AreEquivalent(new[] { old.Id.ToString(), unmutedInTheApp.Id.ToString() },
                chat.Keys.Members($"chat:{chat.ChatId}:muted").ToArray(), "the old keys stay as they were, for a rollback");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task WhilePostgresIsDown_AMuteIsQueued_AndListedOnceItIsBack()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "a");
            var database = chat.Database;
            Enforcer5.Storage.PostgresStorage.UseForTests(null);

            await chat.SendAsync(admin, "/mute", replyTo: message);
            Assert.AreEqual(MemberState.Restricted, chat.Telegram.GetMember(chat.ChatId, member.Id), "the mute itself never waits");
            Assert.AreEqual(1, chat.Hashes.All($"mutes:queue:{BanHistory.Edition}").Count);

            Enforcer5.Storage.PostgresStorage.UseForTests(database);
            await chat.SendAsync(admin, "/mutelist");

            StringAssert.Contains(chat.Replies.Last(), member.Id.ToString());
            CollectionAssert.AreEqual(new[] { (member.Id, "mute", false) }, await Rows(chat));
            Assert.AreEqual(0, chat.Hashes.All($"mutes:queue:{BanHistory.Edition}").Count);
        }

        [TestMethod]
        public async Task AnUpgradedGroup_KeepsItsMutes_UnderItsNewId()
        {
            const long newId = -1009990002;
            await using var chat = await HarnessChat.StartAsync(chatId: -4002);
            var owner = chat.Add("Owner", MemberState.Creator);
            var member = chat.Add("Member");
            await chat.SendAsync(owner, "/mute", replyTo: await chat.SendAsync(member, "a"));

            chat.Deliver(new Message
            {
                Id = chat.Telegram.NextMessageId(), Chat = new Chat { Id = chat.ChatId, Type = ChatType.Group, Title = chat.Title },
                From = owner.ToUser(), Date = chat.Clock.UtcNow, MigrateToChatId = newId
            });
            await chat.SettleAsync();

            Assert.AreEqual(0, (await Rows(chat)).Length);
            CollectionAssert.AreEqual(new[] { (member.Id, "mute", false) }, await Rows(chat, newId));
            CollectionAssert.AreEqual(new[] { member.Id }, (await Enforcer5.Data.Repositories.Mutes.GetMutedAsync(newId)).ToArray(),
                "imported there already: the old keys renamed to the new id are not imported again");
            AssertNoErrors(chat);
        }
    }
}
