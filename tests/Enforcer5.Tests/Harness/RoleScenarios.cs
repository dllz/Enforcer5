using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Data;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// The lists that moved from Redis sets to PostgreSQL (rollout step 14), driven through their commands: /auth,
    /// /elevate, /blockelevate, /disablewatch, /adminoff, /reportoff, /pingme, /makelangadmin, /bangroup. Needs the test
    /// database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class RoleScenarios
    {
        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static async Task<long> Count(HarnessChat chat, string sql)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return await connection.ExecuteScalarAsync<long>(sql);
        }

        [TestMethod]
        public async Task TheCreatorAuthorizes_TheAuthorizedElevates_AndTheModeratorStaysElevated()
        {
            await using var chat = await HarnessChat.StartAsync();
            var creator = chat.Add("Creator", MemberState.Creator);
            var helper = chat.Add("Helper");
            var member = chat.Add("Member");

            await chat.SendAsync(creator, $"/auth {helper.Id}");
            Assert.IsTrue(await Roles.ContainsAsync(chat.ChatId, RoleList.Authorized, helper.Id));
            await chat.SendAsync(helper, $"/elevate {member.Id}");

            CollectionAssert.AreEqual(new[] { member.Id }, chat.RoleMembers(RoleList.Moderator));
            Assert.AreEqual(1L, await Count(chat, "SELECT count(*) FROM group_role_assignments WHERE role = 'moderator' AND revoked_at IS NULL"));
            // The Redis flag is only a cache now: without it the moderator is still elevated.
            chat.Keys[$"chat:{chat.ChatId}:adminses:{member.Id}"] = null;
            Assert.IsTrue(await Methods.IsGroupAdminAsync(member.Id, chat.ChatId));

            await chat.SendAsync(creator, $"/deelevate {member.Id}");
            await chat.SendAsync(creator, $"/deauth {helper.Id}");
            Assert.AreEqual(0, chat.RoleMembers(RoleList.Moderator).Length);
            Assert.IsFalse(await Methods.IsGroupAdminAsync(member.Id, chat.ChatId));
            Assert.IsFalse(await Roles.ContainsAsync(chat.ChatId, RoleList.Authorized, helper.Id));
            Assert.AreEqual(2L, await Count(chat, "SELECT count(*) FROM group_role_assignments WHERE revoked_at IS NOT NULL"),
                "both kept as ended periods");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ABlockedAdmin_CannotElevate()
        {
            await using var chat = await HarnessChat.StartAsync();
            var creator = chat.Add("Creator", MemberState.Creator);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");

            await chat.SendAsync(creator, $"/blockelevate {admin.Id}");
            await chat.SendAsync(admin, $"/elevate {member.Id}");
            Assert.IsNull(chat.Keys[$"chat:{chat.ChatId}:adminses:{member.Id}"]);

            await chat.SendAsync(creator, $"/unblockelevate {admin.Id}");
            await chat.SendAsync(admin, $"/elevate {member.Id}");
            Assert.AreEqual("true", chat.Keys[$"chat:{chat.ChatId}:adminses:{member.Id}"], "for half an hour");
            Assert.AreEqual(0, chat.RoleMembers(RoleList.Moderator).Length, "not for good");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AGroupsOldSets_AreImportedOnItsFirstUse_AndNeverWrittenAgain()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var exempt = chat.Add("Exempt");
            var subscriber = chat.Add("Subscriber");
            // Left in Redis before the deploy.
            chat.Keys.AddToSet($"chat:{chat.ChatId}:watch", exempt.Id.ToString());
            chat.Keys.AddToSet($"chat:{chat.ChatId}:tagall2", subscriber.Id.ToString());
            chat.Keys.AddToSet($"chat:{chat.ChatId}:adminOff", admin.Id.ToString());

            await chat.SendAsync(exempt, "hello");
            await chat.SettleAsync();

            CollectionAssert.AreEqual(new[] { exempt.Id }, chat.RoleMembers(RoleList.Exempt));
            CollectionAssert.AreEqual(new[] { subscriber.Id }, chat.RoleMembers(RoleList.PingMe));
            Assert.IsTrue(await Roles.ContainsAsync(chat.ChatId, RoleList.AdminReportsOff, admin.Id));
            Assert.AreEqual(1L, await Count(chat, $"SELECT count(*) FROM storage_imports WHERE name = 'roles:{chat.ChatId}'"));

            await chat.SendAsync(admin, $"/enablewatch {exempt.Id}");
            await chat.SendAsync(subscriber, "/unpingme");
            Assert.AreEqual(0, chat.RoleMembers(RoleList.Exempt).Length);
            Assert.AreEqual(0, chat.RoleMembers(RoleList.PingMe).Length);
            CollectionAssert.AreEqual(new[] { exempt.Id.ToString() }, chat.Keys.Members($"chat:{chat.ChatId}:watch").ToArray(),
                "the old set is left as it was");

            // A restart reads PostgreSQL, and does not import the old sets again.
            await chat.CrashAndRestartAsync();
            Assert.AreEqual(0, chat.RoleMembers(RoleList.Exempt).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ReportBlockedMembers_AndAdminsWhoTurnedReportsOff()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var quiet = chat.Add("Quiet admin", MemberState.Administrator);
            var reporter = chat.Add("Reporter");
            var spammer = chat.Add("Spammer");
            var post = await chat.SendAsync(spammer, "spam");
            var mine = await chat.SendAsync(reporter, "me");

            await chat.SendAsync(admin, $"/adminoff {quiet.Id}");
            await chat.SendAsync(reporter, "@admin spam", replyTo: post);
            Assert.IsTrue(chat.Telegram.SentMessages.Any(m => m.Chat.Id == admin.Id), "the admin is told");
            Assert.IsFalse(chat.Telegram.SentMessages.Any(m => m.Chat.Id == quiet.Id), "not the one who turned it off");

            await chat.SendAsync(admin, "/reportoff", replyTo: mine);
            var before = chat.Telegram.SentMessages.Count(m => m.Chat.Id == admin.Id);
            await chat.SendAsync(reporter, "@admin again", replyTo: post);
            Assert.AreEqual(before, chat.Telegram.SentMessages.Count(m => m.Chat.Id == admin.Id), "blocked from reporting");
            Assert.AreEqual(1L, await Count(chat, "SELECT count(*) FROM group_user_preferences WHERE reports_blocked"));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task WhilePostgresIsDown_AChangeIsQueued_CountsAtOnce_AndIsWrittenOnceItIsBack()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            await chat.SendAsync(member, "hi"); // the group's old sets imported

            Enforcer5.Storage.PostgresStorage.UseForTests(null);
            try
            {
                await chat.SendAsync(admin, $"/disablewatch {member.Id}");
                Assert.AreEqual(1, Roles.Queued);
                Assert.IsTrue(await Roles.ContainsAsync(chat.ChatId, RoleList.Exempt, member.Id), "counts at once");
            }
            finally
            {
                Enforcer5.Storage.PostgresStorage.UseForTests(chat.Database);
            }
            Assert.AreEqual(0L, await Count(chat, "SELECT count(*) FROM group_user_preferences"));

            await Roles.DrainAsync();

            Assert.AreEqual(0, Roles.Queued);
            Assert.AreEqual(1L, await Count(chat, "SELECT count(*) FROM group_user_preferences WHERE watch_disabled"));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheGlobalLists_LanguageAdminsAndBannedGroups()
        {
            await using var chat = await HarnessChat.StartAsync();
            var dev = chat.Add("Dev", MemberState.Administrator, id: Enforcer5.Models.Constants.Devs[0]);
            var translator = chat.Add("Translator");
            var post = await chat.SendAsync(translator, "hi");
            await chat.RoleAsync(RoleList.LanguageAdmin, dev.Id); // only language admins make others

            await chat.SendAsync(dev, "/makelangadmin", replyTo: post);
            Assert.IsTrue(Methods.IsLangAdmin(translator.Id));
            Assert.AreEqual(2L, await Count(chat, "SELECT count(*) FROM global_role_assignments WHERE role = 'language_admin' AND revoked_at IS NULL"), "the dev and the translator");
            await chat.SendAsync(dev, "/removelangadmin", replyTo: post);
            Assert.IsFalse(Methods.IsLangAdmin(translator.Id));

            await chat.SendAsync(dev, $"/bangroup {translator.Id}");
            Assert.IsTrue(await Roles.ContainsAsync(0, RoleList.BotBanned, translator.Id));
            var replies = chat.Replies.Count;
            await chat.SendAsync(translator, "/rules");
            Assert.AreEqual(replies, chat.Replies.Count, "ignored");
            await chat.SendAsync(dev, $"/unbangroup {translator.Id}");
            Assert.IsFalse(await Roles.ContainsAsync(0, RoleList.BotBanned, translator.Id));
            AssertNoErrors(chat);
        }
    }
}
