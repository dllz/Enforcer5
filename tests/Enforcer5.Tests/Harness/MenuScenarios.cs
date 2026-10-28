using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Data;
using Enforcer5.Detection;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Detection step 6: the settings menu reached from a group, from the private chat (Telegram's group
    /// picker) and from the deep link when the admin never started the bot; the Detection screen (watching
    /// only, going live, how long data is kept); /privacy; and /dashboard gone. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Detection")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class MenuScenarios
    {
        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static string S(HarnessChat chat, string key, params object[] args) =>
            Methods.GetLocaleString(Methods.GetGroupLanguage(chat.ChatId).Doc, key, args);

        private static Message LastPrivate(HarnessChat chat, HarnessUser user) =>
            chat.Telegram.SentMessages.Last(m => m.Chat.Id == user.Id);

        private static Task<DetectionPolicy> Stored(HarnessChat chat) =>
            new PostgresDetectionSettingsRepository(() => chat.Database).LoadAsync(chat.ChatId);

        private static InlineKeyboardButton[] Buttons(Message message) =>
            message.ReplyMarkup?.InlineKeyboard.SelectMany(r => r).ToArray() ?? Array.Empty<InlineKeyboardButton>();

        private static string LastEditText(HarnessChat chat, HarnessUser user) =>
            chat.Telegram.Accepted.Last(c => c.Method == "editMessageText" && c.ChatId == user.Id).Text;

        [TestMethod]
        public async Task SlashMenu_InAGroup_SendsTheMenuInPrivate_WithDetectionFirst()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);

            await chat.SendAsync(admin, "/menu");

            var menu = LastPrivate(chat, admin);
            StringAssert.Contains(menu.Text, chat.Title);
            var buttons = Buttons(menu);
            Assert.AreEqual($"detectionmenu:{chat.ChatId}", buttons[0].CallbackData);
            Assert.AreEqual(KeyboardButtonStyle.Primary, buttons[0].Style);
            Assert.IsFalse(buttons.Any(b => b.CallbackData?.StartsWith("dashboard") == true));
            Assert.AreEqual(S(chat, "botPm"), chat.Replies.Last());
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task SlashMenu_ForAnAdminWhoNeverStartedTheBot_OffersOneTapThatOpensIt()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            chat.Telegram.SetNotStarted(admin.Id);

            await chat.SendAsync(admin, "/menu");

            var reply = chat.Telegram.SentMessages.Last(m => m.Chat.Id == chat.ChatId);
            Assert.AreEqual(S(chat, "menuStartFirst"), reply.Text);
            var open = Buttons(reply).Single();
            Assert.AreEqual($"https://t.me/{HarnessChat.BotUser.Username}?start=menu_{chat.ChatId}", open.Url);

            // They tap it: Telegram starts the bot with the payload.
            chat.Telegram.SetNotStarted(admin.Id, false);
            await chat.PostAsync(new Message
            {
                Id = chat.Telegram.NextMessageId(),
                Chat = new Chat { Id = admin.Id, Type = ChatType.Private },
                From = admin.ToUser(),
                Date = chat.Clock.UtcNow,
                Text = $"/start menu_{chat.ChatId}"
            });
            StringAssert.Contains(LastPrivate(chat, admin).Text, chat.Title);
            Assert.AreEqual($"detectionmenu:{chat.ChatId}", Buttons(LastPrivate(chat, admin))[0].CallbackData);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task SlashMenu_InPrivate_LetsTheAdminPickTheGroup()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            Message Private(HarnessUser from, string text = null, ChatShared shared = null) => new Message
            {
                Id = chat.Telegram.NextMessageId(),
                Chat = new Chat { Id = from.Id, Type = ChatType.Private },
                From = from.ToUser(),
                Date = chat.Clock.UtcNow,
                Text = text,
                ChatShared = shared
            };

            await chat.PostAsync(Private(admin, "/menu"));
            var ask = chat.Telegram.Accepted.Last(c => c.Method == "sendMessage" && c.ChatId == admin.Id);
            var picker = ((ReplyKeyboardMarkup)((Telegram.Bot.Requests.SendMessageRequest)ask.Request).ReplyMarkup).Keyboard.Single().Single();
            Assert.AreEqual(Enforcer5.Commands.MenuChatRequest, picker.RequestChat.RequestId);
            Assert.IsTrue(picker.RequestChat.BotIsMember);
            Assert.IsNotNull(picker.RequestChat.UserAdministratorRights, "only groups they run");

            await chat.PostAsync(Private(admin, shared: new ChatShared { RequestId = Enforcer5.Commands.MenuChatRequest, ChatId = chat.ChatId, Title = chat.Title }));
            Assert.AreEqual($"detectionmenu:{chat.ChatId}", Buttons(LastPrivate(chat, admin))[0].CallbackData);

            // Someone who is no admin (any more) gets no menu.
            var before = chat.Telegram.SentMessages.Count(m => m.Chat.Id == member.Id);
            await chat.PostAsync(Private(member, shared: new ChatShared { RequestId = Enforcer5.Commands.MenuChatRequest, ChatId = chat.ChatId, Title = chat.Title }));
            var sent = chat.Telegram.SentMessages.Where(m => m.Chat.Id == member.Id).Skip(before).ToList();
            Assert.AreEqual(1, sent.Count);
            Assert.AreEqual(0, Buttons(sent[0]).Length);
            AssertNoErrors(chat);
        }

        private static Message PrivateMenu(HarnessChat chat, HarnessUser admin) => new Message
        {
            Id = chat.Telegram.NextMessageId(),
            Chat = new Chat { Id = admin.Id, Type = ChatType.Private },
            From = HarnessChat.BotUser,
            Text = "Settings"
        };

        [TestMethod]
        public async Task TheDetectionScreen_SaysWhatWatchingFound_AndGoesLive()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var message = await chat.SendAsync(member, "hello");
            var sign = SignCatalog.AddForTests("test_menu", 3, "kick");
            await DetectionEngine.HitAsync(chat.ChatId, member.Id, message, new SignHit(sign.Name));
            await chat.SettleAsync();

            await chat.PressDataAsync(admin, PrivateMenu(chat, admin), $"detectionmenu:{chat.ChatId}");
            var text = LastEditText(chat, admin);
            var until = (await Stored(chat)).Engine.ObserveUntil.Value;
            StringAssert.Contains(text, S(chat, "detectionObserving", until.UtcDateTime.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture), 1, 1));
            StringAssert.Contains(text, S(chat, "retention365"));

            await chat.PressDataAsync(admin, PrivateMenu(chat, admin), $"detectionmode:{chat.ChatId}:extend");
            Assert.AreEqual(until.AddDays(7), (await Stored(chat)).Engine.ObserveUntil);

            await chat.PressDataAsync(admin, PrivateMenu(chat, admin), $"detectionmode:{chat.ChatId}:live");
            Assert.IsFalse((await Stored(chat)).Engine.Observing(chat.Clock.GetUtcNow()));
            StringAssert.Contains(LastEditText(chat, admin), "✅");

            await chat.PressDataAsync(admin, PrivateMenu(chat, admin), $"detectionmode:{chat.ChatId}:observe");
            Assert.IsTrue((await Stored(chat)).Engine.Observing(chat.Clock.GetUtcNow()));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task DataKept_CyclesThroughTheThreeChoices_AndSurvivesARestart()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);

            await chat.PressDataAsync(admin, PrivateMenu(chat, admin), $"detectionretention:{chat.ChatId}");
            Assert.AreEqual(730, (await Stored(chat)).Engine.RetentionDays);
            await chat.PressDataAsync(admin, PrivateMenu(chat, admin), $"detectionretention:{chat.ChatId}");
            Assert.AreEqual(90, (await Stored(chat)).Engine.RetentionDays);

            await chat.CrashAndRestartAsync();
            Assert.AreEqual(90, DetectionSettings.For(chat.ChatId).Engine.RetentionDays);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task OnlyAdminsCanChangeDetection()
        {
            await using var chat = await HarnessChat.StartAsync();
            var member = chat.Add("Member");
            await chat.SendAsync(member, "hi");

            await chat.PressDataAsync(member, PrivateMenu(chat, member), $"detectionmode:{chat.ChatId}:live");
            await chat.PressDataAsync(member, PrivateMenu(chat, member), $"detectionretention:{chat.ChatId}");

            var stored = await Stored(chat);
            Assert.IsTrue(stored.Engine.Observing(chat.Clock.GetUtcNow()));
            Assert.AreEqual(365, stored.Engine.RetentionDays);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task GroupSettings_TheNameOfASettingTogglesItToo()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await chat.SendAsync(admin, "/menu");
            await chat.PressDataAsync(admin, LastPrivate(chat, admin), $"openGroupMenu:{chat.ChatId}");
            var groupMenu = chat.Telegram.Accepted.Last(c => c.Method == "editMessageText" && c.ChatId == admin.Id);
            var markup = (InlineKeyboardMarkup)((Telegram.Bot.Requests.EditMessageTextRequest)groupMenu.Request).ReplyMarkup;
            var row = markup.InlineKeyboard.First(r => r.Any(b => b.CallbackData == $"menuMuteOnJoin:{chat.ChatId}"));
            Assert.IsTrue(row.All(b => b.CallbackData == $"menuMuteOnJoin:{chat.ChatId}"), "name and value both toggle");

            await chat.PressDataAsync(admin, PrivateMenu(chat, admin), row.First().CallbackData);
            Assert.IsTrue((await Stored(chat)).MuteOnJoin);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Privacy_WorksInGroupsAndInPrivate_AndDashboardIsGone()
        {
            await using var chat = await HarnessChat.StartAsync();
            var member = chat.Add("Member");

            await chat.SendAsync(member, "/privacy");
            Assert.AreEqual(S(chat, "privacyText"), chat.Replies.Last());
            StringAssert.Contains(chat.Replies.Last(), "90");

            var before = chat.Telegram.SentMessages.Count;
            await chat.SendAsync(member, "/dashboard");
            Assert.AreEqual(before, chat.Telegram.SentMessages.Count, "no such command");
            AssertNoErrors(chat);
        }
    }
}
