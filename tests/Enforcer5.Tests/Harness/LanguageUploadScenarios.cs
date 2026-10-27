using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using Enforcer5.LanguageUpload;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// A language upload from Esme to the end: the zip, one message in the group, the Review menu only the language
    /// admin sees (ephemeral), and the file going live. Needs the test database (the harness does).
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class LanguageUploadScenarios
    {
        private string _root;
        private string _live;
        private Func<string> _previousLive, _previousStaging;

        [TestInitialize]
        public void Init()
        {
            _root = Path.Combine(Path.GetTempPath(), "enf-upload-scenario-" + Guid.NewGuid().ToString("N"));
            _live = Path.Combine(_root, "Languages");
            Directory.CreateDirectory(_live);
            File.WriteAllText(Path.Combine(_live, "English.xml"),
                @"<strings><language name=""English"" base=""English""/>
                    <string key=""Plain""><value>Hello</value></string>
                    <string key=""WithName""><value>Hello {0}</value></string>
                    <string key=""Other""><value>Bye</value></string>
                    <string key=""Gif"" isgif=""true""><value>gif</value></string></strings>");
            _previousLive = LanguageUploads.LiveDirectory;
            _previousStaging = LanguageUploads.StagingDirectory;
            LanguageUploads.LiveDirectory = () => _live;
            LanguageUploads.StagingDirectory = () => Path.Combine(_root, "Staging");
        }

        [TestCleanup]
        public void Cleanup()
        {
            LanguageUploads.LiveDirectory = _previousLive;
            LanguageUploads.StagingDirectory = _previousStaging;
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [TestMethod]
        public async Task EsmesZip_ReviewedByALanguageAdmin_GoesLive()
        {
            await using var chat = await HarnessChat.StartAsync();
            var esme = chat.Add("Esme the helper", username: "esmehelbot", id: BotSenderFilter.EsmeBotId, isBot: true);
            var admin = chat.Add("Lang admin");
            var member = chat.Add("Someone");
            chat.Keys.AddToSet("langAdmins", admin.Id.ToString());

            var zip = LanguageUploadTests.Zip(
                ("German.xml", LanguageUploadTests.German()),
                ("Spanish.xml", LanguageUploadTests.German("Español", "Spanish", withName: "Hola")),
                ("Broken.xml", "<strings><language"));
            var doc = await chat.PostAsync(chat.Document(esme, "langs.zip", zip.ToArray()));
            await chat.SendAsync(esme, "/uploadlanguage@" + HarnessChat.BotUser.Username.ToLowerInvariant(), doc);

            var posted = chat.LastBotMessage;
            var id = IdOf(posted);
            StringAssert.Contains(posted.Text, "3 files");
            StringAssert.Contains(posted.Text, "1 clean");

            // Someone else's tap is answered, and opens nothing.
            await chat.PressAsync(member, posted, "lb:");
            Assert.IsTrue(chat.Telegram.Calls.Any(c => c.Method == "answerCallbackQuery" && c.Text?.Contains("Only language admins") == true));
            Assert.AreEqual(0, chat.Telegram.EphemeralFor(chat.ChatId, member.Id).Count);

            await chat.PressAsync(admin, posted, "lb:");
            var menu = chat.Telegram.EphemeralFor(chat.ChatId, admin.Id).Single();
            StringAssert.Contains(menu.Text, "WithName: Missing {0}");
            StringAssert.Contains(menu.Text, "Broken.xml");

            await chat.PressAsync(admin, menu, BatchMenu.Data(id, BatchMenu.Ops.Ask, (int)BatchAction.UploadClean));
            StringAssert.Contains(menu.Text, "Upload 1 file live?");
            await chat.PressAsync(admin, menu, BatchMenu.Data(id, BatchMenu.Ops.Do, (int)BatchAction.UploadClean));

            Assert.AreEqual(LanguageUploadTests.German(), File.ReadAllText(Path.Combine(_live, "German.xml")));
            Assert.IsFalse(File.Exists(Path.Combine(_live, "Spanish.xml")), "its warning was not accepted");
            Assert.IsFalse(File.Exists(Path.Combine(_live, "Broken.xml")));
            StringAssert.Contains(posted.Text, "Uploaded 1 of 3");
            StringAssert.Contains(menu.Text, "Uploaded 1 of 3 by Lang admin.");
            Assert.IsTrue(menu.ReplyMarkup == null || !menu.ReplyMarkup.InlineKeyboard.Any(), "nothing left to tap");

            // A second tap on the decided upload changes nothing.
            await chat.PressDataAsync(admin, menu, BatchMenu.Data(id, BatchMenu.Ops.Do, (int)BatchAction.UploadClean));
            Assert.AreEqual(0, chat.Errors.Count, string.Join("\n", chat.Errors));
            Assert.AreEqual(0, chat.Telegram.LimitRefusals.Count);
        }

        [TestMethod]
        public async Task AnAcceptedWarning_LetsTheFileThrough()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Lang admin");
            chat.Keys.AddToSet("langAdmins", admin.Id.ToString());

            var xml = System.Text.Encoding.UTF8.GetBytes(LanguageUploadTests.German("Español", "Spanish", withName: "Hola"));
            var doc = await chat.PostAsync(chat.Document(admin, "Spanish.xml", xml));
            await chat.SendAsync(admin, "/uploadlanguage", doc);
            var posted = chat.LastBotMessage;
            var id = IdOf(posted);

            await chat.PressAsync(admin, posted, "lb:");
            var menu = chat.Telegram.EphemeralFor(chat.ChatId, admin.Id).Single();
            Assert.IsFalse(menu.ReplyMarkup.InlineKeyboard.SelectMany(r => r).Any(b => b.CallbackData == BatchMenu.Data(id, BatchMenu.Ops.Ask, 0)),
                "nothing is clean");
            await chat.PressAsync(admin, menu, BatchMenu.Data(id, BatchMenu.Ops.Issues));
            await chat.PressAsync(admin, menu, BatchMenu.Data(id, BatchMenu.Ops.ToggleIssue, 0));
            await chat.PressAsync(admin, menu, BatchMenu.Data(id, BatchMenu.Ops.Ask, (int)BatchAction.UploadSelected));
            await chat.PressAsync(admin, menu, BatchMenu.Data(id, BatchMenu.Ops.Do, (int)BatchAction.UploadSelected));

            Assert.IsTrue(File.Exists(Path.Combine(_live, "Spanish.xml")));
            StringAssert.Contains(posted.Text, "Uploaded 1 of 1");

            // The full report comes as a file only the admin sees; Close removes the menu.
            await chat.PressDataAsync(admin, menu, BatchMenu.Data(id, BatchMenu.Ops.Report));
            Assert.IsTrue(chat.Telegram.Calls.Any(c => c.Method == "sendDocument" && c.Succeeded));
            await chat.PressDataAsync(admin, menu, BatchMenu.Data(id, BatchMenu.Ops.Close));
            Assert.AreEqual(0, chat.Telegram.EphemeralFor(chat.ChatId, admin.Id).Count);
            Assert.AreEqual(0, chat.Errors.Count, string.Join("\n", chat.Errors));
        }

        [TestMethod]
        public async Task NotAFile_IsExplained()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Lang admin");
            chat.Keys.AddToSet("langAdmins", admin.Id.ToString());

            var doc = await chat.PostAsync(chat.Document(admin, "notes.txt", new byte[] { 1 }));
            await chat.SendAsync(admin, "/uploadlanguage", doc);
            StringAssert.Contains(chat.LastBotMessage.Text, "Reply to an .xml file, or a .zip of them");
            var zip = await chat.PostAsync(chat.Document(admin, "bad.zip", new byte[] { 1, 2, 3 }));
            await chat.SendAsync(admin, "/uploadlanguage", zip);
            StringAssert.Contains(chat.LastBotMessage.Text, "cannot be opened");
            Assert.AreEqual(0, chat.Errors.Count, string.Join("\n", chat.Errors));
        }

        /// <summary>The batch id, from the Review button under the group message.</summary>
        private static string IdOf(Telegram.Bot.Types.Message posted)
        {
            var data = posted.ReplyMarkup.InlineKeyboard.SelectMany(r => r).First(b => b.CallbackData?.StartsWith("lb:") == true).CallbackData;
            Assert.IsTrue(BatchMenu.TryParse(data.Split(':'), out var id, out _, out _));
            return id;
        }
    }
}
