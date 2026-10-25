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
    }
}
