using System;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Detection;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Moq;
using StackExchange.Redis;
using Telegram.Bot.Types;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// A group's admin chat (Detection/AdminChats): the bot is there to talk to the admins, never to moderate.
    /// Nothing is enforced there, group commands get one line saying so, extras work (its own and the group's),
    /// and the premium edition stays in it, also while it is being linked. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class AdminChatScenarios
    {
        private const long AdminChatId = -1009990001;

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static string S(HarnessChat chat, string key, params object[] args) =>
            Methods.GetLocaleString(Methods.GetGroupLanguage(chat.ChatId).Doc, key, args);

        /// <summary>The kick for a long text, so a message that should be left alone in the admin chat would not be.</summary>
        private static void KickLongTexts(HarnessChat chat)
        {
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "enabled"] = "yes";
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "maxlength"] = "50";
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "maxlines"] = "20";
            chat.Hashes[$"chat:{chat.ChatId}:antitextlengthsettings", "action"] = "kick";
        }

        private static readonly string LongText = new string('x', 30) + " " + new string('y', 40);

        /// <summary>Links <paramref name="adminChat"/> as the group's admin chat, as the Reports menu's button does.</summary>
        private static async Task LinkAsync(HarnessChat group, HarnessChat adminChat, HarnessUser admin, string token = "t1")
        {
            await Redis.db.StringSetAsync(Enforcer5.Commands.AdminChatTokenKey(token), $"{group.ChatId}:{admin.Id}");
            await adminChat.SendAsync(admin, $"/start {Enforcer5.Commands.AdminChatStart}{token}");
            Assert.AreEqual(S(group, "adminChatLinked", group.Title), adminChat.Replies.Last());
            Assert.IsTrue(AdminChats.IsAdminChat(adminChat.ChatId));
        }

        private static async Task<(HarnessChat Group, HarnessChat AdminChat, HarnessUser Admin, HarnessUser Member)> SetUpAsync()
        {
            var group = await HarnessChat.StartAsync();
            var admin = group.Add("Admin", MemberState.Administrator);
            var adminChat = group.Group(AdminChatId, "Admins only");
            var member = adminChat.Add("Helper");
            group.Telegram.SetMember(AdminChatId, admin.Id, MemberState.Administrator);
            KickLongTexts(group);
            KickLongTexts(adminChat);
            await LinkAsync(group, adminChat, admin);
            return (group, adminChat, admin, member);
        }

        [TestMethod]
        public async Task InAnAdminChat_NothingIsEnforced()
        {
            var (group, adminChat, admin, member) = await SetUpAsync();
            await using var _ = group;
            var post = await adminChat.SendAsync(member, LongText);
            var joiner = adminChat.Add("Joiner", MemberState.Left);
            await adminChat.PostAsync(adminChat.Join(joiner));
            await adminChat.SendAsync(member, "@admin look at this");
            await adminChat.AdvanceAsync(TimeSpan.FromSeconds(10));

            Assert.AreEqual(MemberState.Member, group.Telegram.GetMember(AdminChatId, member.Id), "not kicked for a long text");
            Assert.IsFalse(group.Telegram.Accepted.Any(c => c.ChatId == AdminChatId &&
                c.Method is "banChatMember" or "restrictChatMember" or "deleteMessage"), "no enforcement of any kind");
            Assert.AreEqual(1, adminChat.Replies.Count, "nothing said but the link: no welcome, no @admin");
            Assert.IsFalse(group.Keys.Keys.Any(k => k.StartsWith($"{AdminChatId}:users:")), "no messages counted");

            // A group command: one line saying what the bot is for here, and nothing done.
            await adminChat.SendAsync(admin, "/warn", replyTo: post);
            Assert.AreEqual(S(group, "adminChatMode", group.Title), adminChat.Replies.Last());
            Assert.IsNull(group.Hashes[$"chat:{AdminChatId}:warns", member.Id.ToString()]);
            AssertNoErrors(group);
        }

        [TestMethod]
        public async Task Extras_TheAdminChatsOwnAndTheGroups_Work()
        {
            var (group, adminChat, admin, member) = await SetUpAsync();
            await using var _ = group;
            group.Hashes[$"chat:{group.ChatId}:extra", "#rules"] = "Be nice";
            group.Hashes[$"chat:{group.ChatId}:extra", "#shared"] = "From the group";
            group.Hashes[$"chat:{AdminChatId}:extra", "#shift"] = "Rota";
            group.Hashes[$"chat:{AdminChatId}:extra", "#shared"] = "From the admin chat";

            await adminChat.SendAsync(member, "#rules");
            Assert.AreEqual("Be nice", adminChat.Replies.Last());
            await adminChat.SendAsync(member, "#shift");
            Assert.AreEqual("Rota", adminChat.Replies.Last());
            await adminChat.SendAsync(member, "#shared");
            Assert.AreEqual("From the admin chat", adminChat.Replies.Last(), "the admin chat's own first");

            await adminChat.SendAsync(member, "/extralist");
            var list = adminChat.Replies.Last();
            StringAssert.Contains(list, S(group, "extraListAdminChat"));
            StringAssert.Contains(list, S(group, "extraListGroup", group.Title));
            Assert.IsTrue(list.IndexOf("#shift", StringComparison.Ordinal) < list.IndexOf(group.Title, StringComparison.Ordinal));
            Assert.IsTrue(list.IndexOf("#rules", StringComparison.Ordinal) > list.IndexOf(group.Title, StringComparison.Ordinal));

            // Adding and deleting in the admin chat touches its own extras only.
            await adminChat.SendAsync(admin, "/extra #new Hello");
            await adminChat.SendAsync(admin, "/extradel #rules");
            Assert.IsNotNull(group.Hashes[$"chat:{AdminChatId}:extra", "#new"]);
            Assert.IsNull(group.Hashes[$"chat:{group.ChatId}:extra", "#new"]);
            Assert.AreEqual("Be nice", group.Hashes[$"chat:{group.ChatId}:extra", "#rules"]);
            AssertNoErrors(group);
        }

        [TestMethod]
        public async Task TheGroupItself_IsStillModerated()
        {
            var (group, adminChat, admin, _) = await SetUpAsync();
            await using var __ = group;
            var member = group.Add("Member");

            await group.SendAsync(member, LongText);
            await group.AdvanceAsync(TimeSpan.FromSeconds(10));

            Assert.AreNotEqual(MemberState.Member, group.Telegram.GetMember(group.ChatId, member.Id));
        }

        [TestMethod]
        public async Task OnceUnlinked_ItIsNoAdminChatAnyMore()
        {
            var (group, adminChat, admin, member) = await SetUpAsync();
            await using var _ = group;

#if PREMIUM
            NotPremium(group, AdminChatId);
#endif
            await group.SendAsync(admin, "/menu");
            var menu = group.Telegram.SentMessages.Last(m => m.Chat.Id == admin.Id);
            await group.PressDataAsync(admin, menu, $"reportsunlink:{group.ChatId}");

            Assert.IsFalse(AdminChats.IsAdminChat(AdminChatId));
#if PREMIUM
            Assert.IsTrue(group.Telegram.Accepted.Any(c => c.Method == "leaveChat" && c.ChatId == AdminChatId), "premium leaves");
            Assert.IsFalse(group.Telegram.Accepted.Any(c => c.Method == "sendMessage" && c.ChatId == AdminChatId &&
                c.Text?.Contains("no longer active") == true), "quietly");
#else
            await adminChat.SendAsync(member, LongText);
            await adminChat.AdvanceAsync(TimeSpan.FromSeconds(10));
            Assert.AreNotEqual(MemberState.Member, group.Telegram.GetMember(AdminChatId, member.Id), "a group again");
#endif
            AssertNoErrors(group);
        }

#if PREMIUM
        /// <summary>The admin chat is no premium group; the harness answers every premium check yes.</summary>
        private static void NotPremium(HarnessChat chat, long chatId) =>
            chat.Redis.Setup(r => r.SetContainsAsync(It.Is<RedisKey>(k => k == "premiumBot"), It.Is<RedisValue>(v => v == chatId),
                It.IsAny<CommandFlags>())).ReturnsAsync(false);

        private static HarnessUser Me => new(HarnessChat.BotUser.Id, HarnessChat.BotUser.FirstName, HarnessChat.BotUser.Username, IsBot: true);

        [TestMethod]
        public async Task Premium_AddedWithTheLink_StaysAndLinksTheChat()
        {
            await using var group = await HarnessChat.StartAsync();
            var admin = group.Add("Admin", MemberState.Administrator);
            var adminChat = group.Group(AdminChatId, "Admins only");
            group.Telegram.SetMember(AdminChatId, admin.Id, MemberState.Administrator);
            NotPremium(group, AdminChatId);

            await adminChat.PostAsync(adminChat.Join(Me, addedBy: admin));
            group.Telegram.SetMember(AdminChatId, Me.Id, MemberState.Administrator);
            await LinkAsync(group, adminChat, admin);
            await adminChat.SendAsync(admin, "hello admins");

            Assert.IsFalse(group.Telegram.Accepted.Any(c => c.Method == "leaveChat"), "stays");
            Assert.IsFalse(adminChat.Replies.Any(r => r.Contains("no longer active")));
            AssertNoErrors(group);
        }

        [TestMethod]
        public async Task Premium_AddedWithoutALink_LeavesAtTheNextMessage()
        {
            await using var group = await HarnessChat.StartAsync();
            var admin = group.Add("Admin", MemberState.Administrator);
            var other = group.Group(AdminChatId, "Some group");
            NotPremium(group, AdminChatId);

            await other.PostAsync(other.Join(Me, addedBy: admin));
            Assert.IsFalse(group.Telegram.Accepted.Any(c => c.Method == "leaveChat"), "waits for a link after being added");
            Assert.AreEqual(0, other.Replies.Count);

            await other.SendAsync(admin, "hi");
            await other.SettleAsync();
            Assert.IsTrue(group.Telegram.Accepted.Any(c => c.Method == "leaveChat" && c.ChatId == AdminChatId));
            Assert.AreEqual(1, other.Replies.Count(r => r.Contains("no longer active")));
        }
#endif
    }
}
