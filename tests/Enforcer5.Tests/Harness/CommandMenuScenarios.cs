using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;

namespace Enforcer5.Tests.Harness
{
    /// <summary>The bot sets its own command menus at startup. Needs the test database (the harness does).</summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class CommandMenuScenarios
    {
        [TestMethod]
        public async Task TheMenusComeFromTheCode_AndASecondStartChangesNothing()
        {
            await using var chat = await HarnessChat.StartAsync();
            // What someone typed into BotFather: replaced, and a German menu nobody translates is removed.
            chat.Telegram.PresetCommands(BotCommandScope.AllGroupChats(), null, new BotCommand { Command = "old", Description = "Old" });
            chat.Telegram.PresetCommands(BotCommandScope.AllGroupChats(), "de", new BotCommand { Command = "alt", Description = "Alt" });

            var first = await CommandMenus.SyncAsync(Bot.Api);

            var menus = chat.Telegram.CommandMenus;
            Assert.AreEqual(0, first.MissingFromEnglish.Count, string.Join(", ", first.MissingFromEnglish));
            CollectionAssert.AreEqual(CommandMenus.GroupCommands, menus["AllGroupChats/"].Select(c => c.Command).ToArray());
            CollectionAssert.AreEqual(CommandMenus.AdminCommands, menus["AllChatAdministrators/"].Select(c => c.Command).ToArray());
            CollectionAssert.AreEqual(CommandMenus.PrivateCommands, menus["AllPrivateChats/"].Select(c => c.Command).ToArray());
            Assert.AreEqual("Ban a user", menus["AllChatAdministrators/"].Single(c => c.Command == "ban").Description);
            Assert.IsFalse(menus.ContainsKey("AllGroupChats/de"), "an untranslated language falls back to English");

            var dev = Models.Constants.Devs[0];
            var devMenu = menus[$"Chat:{dev}/"];
            Assert.IsTrue(devMenu.Any(c => c.Command == "halt" && c.Description == "(dev only) Stop the bot"));
            Assert.IsTrue(devMenu.Any(c => c.Command == "getrekt" && c.Description.StartsWith("(global admin only) ")));
            var globalAdmin = Models.Constants.GlobalAdmins.First(id => !Models.Constants.Devs.Contains(id));
            Assert.IsFalse(menus[$"Chat:{globalAdmin}/"].Any(c => c.Command == "halt"), "global admins do not see dev commands");

            var calls = chat.Telegram.Calls.Count(c => c.Method is "setMyCommands" or "deleteMyCommands");
            var second = await CommandMenus.SyncAsync(Bot.Api);

            Assert.AreEqual(0, second.Changed.Count, string.Join(", ", second.Changed));
            Assert.AreEqual(calls, chat.Telegram.Calls.Count(c => c.Method is "setMyCommands" or "deleteMyCommands"), "only reads");
            Assert.AreEqual(0, chat.Errors.Count, string.Join("\n", chat.Errors));
        }
    }
}
