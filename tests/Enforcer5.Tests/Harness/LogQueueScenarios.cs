using System;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Log channel posts go through LogQueue: an admin's button never waits for the log channel, a busy or
    /// rate limited channel gets every entry later (merged), a restart loses none, and a channel the bot lost
    /// is removed as before. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class LogQueueScenarios
    {
        private const long LogChannel = -1004444;

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static void WithLogChannel(HarnessChat chat)
        {
            chat.Keys.AddToSet("logChatGroups", chat.ChatId.ToString());
            chat.Hashes[$"chat:{chat.ChatId}:settings", "logchat"] = LogChannel.ToString();
        }

        private static TelegramCall[] LogPosts(HarnessChat chat) =>
            chat.Telegram.Accepted.Where(c => c.Method == "sendMessage" && c.ChatId == LogChannel).ToArray();

        [TestMethod]
        public async Task AMenuButton_RedrawsAtOnce_WhileTheLogChannelIsRateLimited_AndTheLogFollows()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            WithLogChannel(chat);
            await chat.SendAsync(admin, "/menu");
            var menu = chat.Telegram.SentMessages.Last(m => m.Chat.Id == admin.Id);
            await chat.PressDataAsync(admin, menu, $"editsmenu:{chat.ChatId}");
            chat.Telegram.StartStorm(c => c.ChatId == LogChannel, TimeSpan.FromMinutes(1), retryAfter: 40);
            var pressed = chat.Clock.UtcNow;
            var before = LogPosts(chat).Length;

            await chat.PressDataAsync(admin, menu, $"editstoggle:{chat.ChatId}:late");

            var redraw = chat.Telegram.Accepted.Last(c => c.Method == "editMessageText" && c.ChatId == admin.Id);
            Assert.AreEqual(pressed, redraw.At.UtcDateTime, "the menu does not wait for the log channel");
            var posts = LogPosts(chat).Skip(before).ToArray();
            Assert.AreEqual(1, posts.Length, "posted once the storm is over");
            Assert.IsTrue(posts[0].At.UtcDateTime >= pressed.AddMinutes(1));
            StringAssert.Contains(posts[0].Text, "changed Late edits in");
            StringAssert.Contains(posts[0].Text, ": On → Off");
            Assert.AreEqual(0, LogQueue.Count);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ABacklog_IsMergedIntoOnePost_AndSurvivesARestart()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            WithLogChannel(chat);
            chat.Telegram.StartStorm(c => c.ChatId == LogChannel, TimeSpan.FromMinutes(10), retryAfter: 600);
            for (var i = 0; i < 5; i++)
                Service.SendToLogChannel(chat.ChatId, Methods.GetGroupLanguage(chat.ChatId).Doc, () => $"entry {i}", linkMessageId: 10 + i);
            await chat.SettleAsync(flush: false);
            Assert.AreEqual(0, LogPosts(chat).Length);

            await chat.CrashAndRestartAsync();
            await chat.AdvanceAsync(TimeSpan.FromMinutes(11));

            var post = LogPosts(chat).Single();
            for (var i = 0; i < 5; i++) StringAssert.Contains(post.Text, $"entry {i}");
            Assert.IsTrue(post.Text.IndexOf("entry 0", StringComparison.Ordinal) < post.Text.IndexOf("entry 4", StringComparison.Ordinal));
            StringAssert.Contains(post.Text, "<a href=");
            Assert.AreEqual(0, LogQueue.Count);
            Assert.AreEqual(0, chat.Hashes.All(Repositories_LogKey()).Count, "the journal is emptied");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task OpeningScreens_IsNotLogged_AndAnActionSaysWhichButton()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            WithLogChannel(chat);
            await chat.SendAsync(admin, "/menu");
            var menu = chat.Telegram.SentMessages.Last(m => m.Chat.Id == admin.Id);
            var before = LogPosts(chat).Length;

            await chat.PressDataAsync(admin, menu, $"detectionmenu:{chat.ChatId}");
            await chat.PressDataAsync(admin, menu, $"editsmenu:{chat.ChatId}");
            await chat.PressDataAsync(admin, menu, $"detectionmenu:{chat.ChatId}");
            Assert.AreEqual(before, LogPosts(chat).Length, "opening screens is not logged");

            var live = menu.ReplyMarkup.InlineKeyboard
                .SelectMany(r => r).FirstOrDefault(b => b.CallbackData == $"detectionmode:{chat.ChatId}:live");
            Assert.IsNotNull(live, "the Detection screen offers Go live");
            await chat.PressDataAsync(admin, menu, live.CallbackData);
            var post = LogPosts(chat).Skip(before).Single();
            StringAssert.Contains(post.Text, $"pressed “{live.Text}” in");
            Assert.IsFalse(post.Text.Contains("detectionmode:"), post.Text);
            AssertNoErrors(chat);
        }

        private static string Repositories_LogKey() => ((Enforcer5.Data.RedisLogJournal)Enforcer5.Data.Repositories.LogJournal).Key;
    }
}
