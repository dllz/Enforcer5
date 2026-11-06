using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Actions;
using Enforcer5.Enforcement;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// The bot muted in, removed from, or upgraded out of a group (Helpers/BotRights, MutedGroups, GroupMigration;
    /// Daniel, 2026-11-06): expected refusals are handled where they happen, never reported, and never asked of
    /// Telegram again and again. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class BotRightsScenarios
    {
        private const long LogChannel = -1006666;

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static Message[] PrivateTo(HarnessChat chat, HarnessUser user) =>
            chat.Telegram.SentMessages.Where(m => m.Chat.Id == user.Id).ToArray();

        private static TelegramCall[] PostsTo(HarnessChat chat, long chatId) =>
            chat.Telegram.Calls.Where(c => c.ChatId == chatId && c.Method.StartsWith("send", StringComparison.Ordinal)).ToArray();

        private static Update BotMemberUpdate(HarnessChat chat, HarnessUser by, ChatMember now) => new Update
        {
            MyChatMember = new ChatMemberUpdated
            {
                Chat = new Chat { Id = chat.ChatId, Type = ChatType.Supergroup, Title = chat.Title },
                From = by.ToUser(),
                Date = chat.Clock.UtcNow,
                OldChatMember = new ChatMemberAdministrator { User = HarnessChat.BotUser, CanRestrictMembers = true },
                NewChatMember = now
            }
        };

        [TestMethod]
        public async Task MutedAndNotAnAdmin_TheBotLeaves_AndTellsWhoCouldHaveFixedIt()
        {
            await using var chat = await HarnessChat.StartAsync();
            var member = chat.Add("Member");
            var owner = chat.Add("Owner", MemberState.Creator);
            var admin = chat.Add("Admin", MemberState.Administrator);
            chat.Telegram.SetBotMuted(chat.ChatId);

            var muted = chat.Clock.UtcNow;
            await chat.SendAsync(member, "/ping");
            await chat.SendAsync(member, "/ping");
            await chat.AdvanceAsync(MutedGroups.LeaveAfter + TimeSpan.FromSeconds(5));
            var left = chat.Telegram.Accepted.Single(c => c.Method == "leaveChat" && c.ChatId == chat.ChatId);
            Assert.IsTrue(left.At.UtcDateTime >= muted + MutedGroups.LeaveAfter, "it looks again first");

            Assert.AreEqual(1, PostsTo(chat, chat.ChatId).Length, "refused once by Telegram, then known: no reply tried again");
            Assert.AreEqual(0, PrivateTo(chat, member).Length, "members are not written to");
            StringAssert.Contains(PrivateTo(chat, owner).Single().Text, "I've left");
            Assert.AreEqual(0, PrivateTo(chat, admin).Length, "only those who could have fixed it, when there are any");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task MutedForAMomentOnly_TheBotStays()
        {
            await using var chat = await HarnessChat.StartAsync();
            var member = chat.Add("Member");
            var owner = chat.Add("Owner", MemberState.Creator);
            chat.Telegram.SetBotMuted(chat.ChatId);
            chat.Deliver(chat.Text(member, "/ping"));
            await chat.SettleAsync(flush: false);

            chat.Telegram.SetBotMuted(chat.ChatId, false);
            chat.Deliver(BotMemberUpdate(chat, owner, new ChatMemberAdministrator
                { User = HarnessChat.BotUser, CanRestrictMembers = true, CanDeleteMessages = true }));
            await chat.AdvanceAsync(MutedGroups.LeaveAfter + TimeSpan.FromSeconds(5));
            await chat.SendAsync(member, "/ping");

            Assert.IsFalse(chat.Telegram.Calls.Any(c => c.Method == "leaveChat"));
            Assert.IsTrue(chat.Telegram.Accepted.Any(c => c.ChatId == chat.ChatId && c.Method == "sendMessage"), "it answers at once");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task NotAnAdmin_ExtraOnly_MembersAreAnswered_AdminCommandsSayItEveryThreeHours()
        {
            await using var chat = await HarnessChat.StartAsync();
            var owner = chat.Add("Owner", MemberState.Creator);
            var member = chat.Add("Member");
            var spammer = chat.Add("Spammer");
            chat.Hashes[$"chat:{chat.ChatId}:settings", "MuteOnJoin"] = "no"; // "no" switches it on
            await GroupConfigs.ChangeAsync(chat.ChatId, Data.ConfigChange.SetContent(Data.ContentKind.Rules, "Be nice"));
            chat.Telegram.SetBotMember(chat.ChatId);
            chat.Deliver(BotMemberUpdate(chat, owner, new ChatMemberMember { User = HarnessChat.BotUser }));
            await chat.SettleAsync();
            var notAdmin = Methods.GetLocaleString(Methods.GetGroupLanguage(chat.ChatId).Doc, "botNotAdmin");

            var spam = await chat.SendAsync(spammer, "spam");
            await chat.SendAsync(owner, "/ban", replyTo: spam);
            await chat.SendAsync(owner, "/ban", replyTo: spam);
            await chat.SendAsync(member, "/rules");
            var joiner = chat.Add("Joiner", MemberState.Left);
            await chat.JoinAsync(joiner);

            Assert.AreEqual(1, chat.Telegram.SentMessages.Count(m => m.Chat.Id == chat.ChatId && m.Text == notAdmin), "once");
            Assert.IsFalse(chat.Telegram.Calls.Any(c => c.Method is "banChatMember" or "restrictChatMember" or "deleteMessage"),
                "nothing is tried that needs an admin");
            Assert.IsTrue(chat.Telegram.SentMessages.Any(m => m.Chat.Id == chat.ChatId && m.Text?.Contains("Be nice") == true),
                "members' commands are answered");

            await chat.AdvanceAsync(MutedGroups.NotAdminEvery);
            await chat.SendAsync(owner, "/ban", replyTo: spam);
            Assert.AreEqual(2, chat.Telegram.SentMessages.Count(m => m.Chat.Id == chat.ChatId && m.Text == notAdmin), "again after 3 hours");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AReplyToADeletedMessage_GoesOutWithoutTheReply()
        {
            await using var chat = await HarnessChat.StartAsync();
            var member = chat.Add("Member");
            var gone = await chat.SendAsync(member, "hello");
            chat.Telegram.MessageDeleted(chat.ChatId, gone.MessageId);

            Bot.SendReply("first", chat.ChatId, gone.MessageId);
            chat.Telegram.IgnoresAllowWithoutReply = true;
            Bot.SendReply("second", chat.ChatId, gone.MessageId);
            await chat.SettleAsync();

            Assert.IsTrue(chat.Telegram.SentMessages.Any(m => m.Chat.Id == chat.ChatId && m.Text == "first"));
            Assert.IsTrue(chat.Telegram.SentMessages.Any(m => m.Chat.Id == chat.ChatId && m.Text == "second"),
                "sent once more without the reply when Telegram still refuses it");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task RemovedFromTheGroup_NothingIsAskedOfTelegramThere()
        {
            await using var chat = await HarnessChat.StartAsync();
            var owner = chat.Add("Owner", MemberState.Creator);
            var member = chat.Add("Member");
            chat.Telegram.SetBotRemoved(chat.ChatId);
            chat.Deliver(BotMemberUpdate(chat, owner, new ChatMemberLeft { User = HarnessChat.BotUser }));
            await chat.SettleAsync();
            var before = chat.Telegram.Calls.Count(c => c.ChatId == chat.ChatId);

            var result = await Task.Run(() => EnforcementQueue.Run(EnforcementJob.Ban(chat.ChatId, member.Id)));
            Bot.Send("anything", chat.ChatId);
            await chat.SettleAsync();

            Assert.AreEqual(Refusal.BotNotAdmin, result.Refusal);
            Assert.AreEqual(before, chat.Telegram.Calls.Count(c => c.ChatId == chat.ChatId), "no call to a group the bot left");
            Assert.AreEqual(0, EnforcementQueue.Count, "nothing queued for it");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ALogChannelTheBotMayNotPostIn_IsUnsetOnce()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Keys.AddToSet("logChatGroups", chat.ChatId.ToString());
            chat.Hashes[$"chat:{chat.ChatId}:settings", "logchat"] = LogChannel.ToString();
            chat.Telegram.SetBotMuted(LogChannel);

            for (var i = 0; i < 3; i++)
                Service.SendToLogChannel(chat.ChatId, Methods.GetGroupLanguage(chat.ChatId).Doc, () => $"entry {i}");
            await chat.SettleAsync();
            Service.SendToLogChannel(chat.ChatId, Methods.GetGroupLanguage(chat.ChatId).Doc, () => "later");
            await chat.SettleAsync();

            Assert.IsNull(GroupConfigs.For(chat.ChatId).LogChat);
            Assert.AreEqual(1, PostsTo(chat, LogChannel).Length, "one refusal, not five tries per post");
            var autoRemoved = Methods.GetLocaleString(Methods.GetGroupLanguage(chat.ChatId).Doc, "logchannelAutoRemoved");
            Assert.AreEqual(1, chat.Telegram.SentMessages.Count(m => m.Chat.Id == chat.ChatId && m.Text == autoRemoved));
            Assert.AreEqual(0, LogQueue.Count);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AWelcomeGifTheGroupDoesNotAllow_GoesOutAsText_AndMuteOnJoinStillMutes()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "type"] = "custom";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "content"] = "Hello $name";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "hasmedia"] = "true";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "media"] = "GIF-FILE-ID";
            chat.Hashes[$"chat:{chat.ChatId}:settings", "MuteOnJoin"] = "no"; // "no" switches it on
            chat.Telegram.SetNoMedia(chat.ChatId);
            var joiner = chat.Add("Joiner", MemberState.Left);

            await chat.JoinAsync(joiner);

            Assert.IsTrue(chat.Telegram.SentMessages.Any(m => m.Chat.Id == chat.ChatId && m.Text?.Contains("Hello") == true));
            Assert.AreEqual(MemberState.Restricted, chat.Telegram.GetMember(chat.ChatId, joiner.Id));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ABatch_StopsWhenTheBotHasNoRights()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var members = Enumerable.Range(0, 5).Select(i => chat.Add($"Member{i}")).ToArray();
            var list = await chat.SendAsync(admin, string.Join("\n", members.Select(m => m.Id)));
            await chat.SendAsync(admin, "/batch ban spam", replyTo: list);
            var preview = chat.Telegram.SentMessages.Last(m => m.Chat.Id == chat.ChatId && m.ReplyMarkup != null &&
                m.ReplyMarkup.InlineKeyboard.SelectMany(r => r).Any(b => b.CallbackData?.Contains(":run:") == true));
            chat.Telegram.SetBotCanRestrict(chat.ChatId, false);

            await chat.PressAsync(admin, preview, $"{Batches.Trigger}:{chat.ChatId}:run:");

            await using var connection = await chat.Database.OpenConnectionAsync();
            Assert.AreEqual("stopped", await connection.QuerySingleAsync<string>("SELECT status FROM batches ORDER BY batch_id DESC LIMIT 1"));
            Assert.AreEqual(1, chat.Telegram.Calls.Count(c => c.Method == "banChatMember"), "stopped at the first refusal");
            Assert.IsTrue(EnforcementQueue.Count <= 1, "not a job per member waiting for the rights");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnUpgradedGroup_KeepsItsSettings_UnderItsNewId()
        {
            const long newId = -1009990001;
            await using var chat = await HarnessChat.StartAsync(chatId: -4001);
            var owner = chat.Add("Owner", MemberState.Creator);
            await GroupConfigs.ChangeAsync(chat.ChatId, Data.ConfigChange.SetLogChat(LogChannel));
            // The new group was seen before the move was: its own (empty) settings give way.
            _ = await GroupConfigs.GetAsync(newId);

            chat.Deliver(new Message
            {
                Id = chat.Telegram.NextMessageId(), Chat = new Chat { Id = chat.ChatId, Type = ChatType.Group, Title = chat.Title },
                From = owner.ToUser(), Date = chat.Clock.UtcNow, MigrateToChatId = newId
            });
            await chat.SettleAsync();

            Assert.AreEqual(LogChannel, (await GroupConfigs.GetAsync(newId)).LogChat);
            await using var connection = await chat.Database.OpenConnectionAsync();
            Assert.AreEqual(0, await connection.QuerySingleAsync<int>(@"
SELECT count(*) FROM group_settings s JOIN groups g ON g.group_id = s.group_id JOIN telegram_chats c ON c.chat_id = g.chat_id
WHERE c.telegram_chat_id = @Old AND s.log_chat_id IS NOT NULL", new { Old = chat.ChatId }), "the settings are the new id's now");

            // The other edition hears of it later, after the old id was seen again: nothing changes.
            GroupMigration.ResetForTests();
            await GroupMigration.MoveAsync(chat.ChatId, newId);
            Assert.AreEqual(LogChannel, (await GroupConfigs.GetAsync(newId)).LogChat);
            AssertNoErrors(chat);
        }
    }
}
