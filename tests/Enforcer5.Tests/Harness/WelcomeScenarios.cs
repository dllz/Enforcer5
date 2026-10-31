using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>The welcome a group sends when someone joins. Needs the test database, like every harness scenario.</summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class WelcomeScenarios
    {
        private static string[] SentDocuments(HarnessChat chat) =>
            chat.Telegram.Calls.Where(c => c.Method == "sendDocument" && c.ChatId == chat.ChatId).Select(c => c.Text).ToArray();

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        [TestMethod]
        public async Task GifWelcome_SendsTheSavedGif()
        {
            // What /welcome saves when an admin replies to a GIF: the file in "media", the
            // command's (empty) argument in "content".
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "type"] = "media";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "media"] = "GIF-FILE-ID";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "hasmedia"] = "True";

            await chat.JoinAsync(new HarnessUser(424243, "Joiner"));

            CollectionAssert.AreEqual(new[] { "GIF-FILE-ID" }, SentDocuments(chat));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task GifWelcomeFromBefore2017Fix_StillReadsContent()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "type"] = "media";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "content"] = "OLD-GIF-FILE-ID";

            await chat.JoinAsync(new HarnessUser(424244, "Joiner"));

            CollectionAssert.AreEqual(new[] { "OLD-GIF-FILE-ID" }, SentDocuments(chat));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task GifWelcomeWithoutAGif_SendsNothing_AndReportsNothing()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "type"] = "media";

            await chat.JoinAsync(new HarnessUser(424245, "Joiner"));

            Assert.AreEqual(0, SentDocuments(chat).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task CustomWelcomeMarkedWithMediaButWithoutAGif_SendsTheText()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "type"] = "custom";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "content"] = "Hello there";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "hasmedia"] = "true";

            await chat.JoinAsync(new HarnessUser(424246, "Joiner"));

            Assert.AreEqual(0, SentDocuments(chat).Length);
            Assert.IsTrue(chat.Replies.Any(r => r.Contains("Hello there")), string.Join("\n", chat.Replies));
            AssertNoErrors(chat);
        }
        private static void CustomWelcome(HarnessChat chat, string text)
        {
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "type"] = "custom";
            chat.Hashes[$"chat:{chat.ChatId}:welcome", "content"] = text;
        }

        [TestMethod]
        public async Task AJoinWithoutAServiceMessage_IsWelcomedFromTheMemberUpdate()
        {
            // Big groups (the support group, The Origin) get no "joined" service message, only the member update.
            await using var chat = await HarnessChat.StartAsync();
            CustomWelcome(chat, "Welcome $name");
            var joiner = chat.Add("Newcomer", MemberState.Left);

            chat.Deliver(chat.MemberJoined(joiner, "https://t.me/+invite"));
            await chat.CompleteAsync();

            Assert.AreEqual(1, chat.Replies.Count(r => r.Contains("Welcome Newcomer")), string.Join("\n", chat.Replies));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AServiceMessageAndAMemberUpdate_WelcomeOnce()
        {
            await using var chat = await HarnessChat.StartAsync();
            CustomWelcome(chat, "Welcome $name");
            var joiner = chat.Add("Newcomer", MemberState.Left);

            chat.Deliver(chat.MemberJoined(joiner, "https://t.me/+invite"));
            chat.Deliver(chat.Join(joiner));
            await chat.CompleteAsync();

            Assert.AreEqual(1, chat.Replies.Count(r => r.Contains("Welcome Newcomer")), string.Join("\n", chat.Replies));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task WelcomeOnAndOff_SwitchTheWelcome_InsteadOfSavingTheWord()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            CustomWelcome(chat, "Welcome $name");

            await chat.SendAsync(admin, "/welcome off");
            Assert.AreEqual("yes", chat.Hashes[$"chat:{chat.ChatId}:settings", "Welcome"]);
            Assert.AreEqual("Welcome $name", chat.Hashes[$"chat:{chat.ChatId}:welcome", "content"]);
            await chat.JoinAsync(chat.Add("Quiet", MemberState.Left));
            Assert.IsFalse(chat.Replies.Any(r => r.Contains("Welcome Quiet")));

            await chat.SendAsync(admin, "/welcome on");
            Assert.AreEqual("no", chat.Hashes[$"chat:{chat.ChatId}:settings", "Welcome"]);
            await chat.JoinAsync(chat.Add("Loud", MemberState.Left));
            Assert.IsTrue(chat.Replies.Any(r => r.Contains("Welcome Loud")), string.Join("\n", chat.Replies));
            AssertNoErrors(chat);
        }
    }
}
