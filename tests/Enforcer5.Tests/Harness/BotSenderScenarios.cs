using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Messages from bots (Bot-to-Bot Communication Mode): only trusted bots are handled, and Esme may
    /// run the language upload without being a lang admin. Needs the test database (the harness does).
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class BotSenderScenarios
    {
        [TestMethod]
        public async Task AnotherBotsCommands_AreIgnored()
        {
            await using var chat = await HarnessChat.StartAsync();
            var otherBot = chat.Add("Some bot", username: "somebot", isBot: true);
            var person = chat.Add("Ann");

            var before = chat.Telegram.SentMessages.Count;
            await chat.SendAsync(otherBot, "/id");
            await chat.SendAsync(otherBot, "/uploadlanguage");
            Assert.AreEqual(before, chat.Telegram.SentMessages.Count, "a bot's commands get no answer");

            await chat.SendAsync(person, "/id");
            Assert.IsTrue(chat.Telegram.SentMessages.Count > before, "a person's do");
            Assert.AreEqual(0, chat.Errors.Count, string.Join("\n", chat.Errors));
        }

        [TestMethod]
        public async Task Esme_RunsTheUpload_AddressedInAnyCase()
        {
            await using var chat = await HarnessChat.StartAsync();
            var esme = chat.Add("Esme the helper", username: "esmehelbot", id: BotSenderFilter.EsmeBotId, isBot: true);

            // Not a reply to a file, so the upload answers with how to use it: it got past the rights check.
            await chat.SendAsync(esme, $"/uploadlanguage@{HarnessChat.BotUser.Username.ToLowerInvariant()}");
            Assert.IsTrue(chat.Telegram.SentMessages.Any(m => m.Chat.Id == chat.ChatId && m.Text.Contains("/uploadlanguage")),
                "Esme's upload command is answered");

            var before = chat.Telegram.SentMessages.Count;
            await chat.SendAsync(esme, "/reloadLang");
            await chat.SendAsync(esme, "/uploadlanguage@someotherbot");
            Assert.AreEqual(before, chat.Telegram.SentMessages.Count, "nothing else, and not another bot's command");
            Assert.AreEqual(0, chat.Errors.Count, string.Join("\n", chat.Errors));
        }
    }
}
