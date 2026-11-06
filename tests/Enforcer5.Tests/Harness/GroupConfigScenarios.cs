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
    /// A group's settings, welcome, texts and extras in PostgreSQL (rollout step 15), driven through commands and the
    /// menu. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class GroupConfigScenarios
    {
        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static async Task<T> Scalar<T>(HarnessChat chat, string sql)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return await connection.ExecuteScalarAsync<T>(sql);
        }

        [TestMethod]
        public async Task TheOldKeys_AreImportedOnTheGroupsFirstUse_AndNeverWritten()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            // As Redis held them before the deploy.
            chat.Hashes[$"chat:{chat.ChatId}:settings", "Rules"] = "no";
            chat.Hashes[$"chat:{chat.ChatId}:settings", "Welcome"] = "yes";
            chat.Keys[$"chat:{chat.ChatId}:rules"] = "Be kind";
            chat.Hashes[$"chat:{chat.ChatId}:extra", "#hi"] = "Hello there";

            await chat.SendAsync(member, "/rules");
            StringAssert.Contains(chat.Replies.Last(), "Be kind", "in the group, as the old Rules = no said");
            await chat.SendAsync(member, "#hi");
            Assert.AreEqual("Hello there", chat.Replies.Last());

            await chat.SendAsync(admin, "/setrules Be nicer");
            await chat.SendAsync(admin, "/extra #bye See you");
            Assert.AreEqual("Be kind", chat.Keys[$"chat:{chat.ChatId}:rules"], "the old key is left as it was");
            Assert.IsNull(chat.Hashes[$"chat:{chat.ChatId}:extra", "#bye"]);
            Assert.AreEqual("Be nicer", await Scalar<string>(chat, "SELECT content FROM group_content_items WHERE content_type = 'rules'"));
            Assert.AreEqual(2L, await Scalar<long>(chat, "SELECT count(*) FROM custom_commands"));
            Assert.IsFalse(await Scalar<bool>(chat, "SELECT welcome_enabled FROM group_settings"));

            // After a restart, from PostgreSQL.
            await chat.CrashAndRestartAsync();
            await chat.SendAsync(member, "#bye");
            Assert.AreEqual("See you", chat.Replies.Last());
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheMenusReportToggle_TurnsReportsOff_AndOn()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var spam = await chat.SendAsync(chat.Add("Spammer"), "spam");
            await chat.SendAsync(member, "@admin look", replyTo: spam);
            Assert.IsTrue(chat.Telegram.SentMessages.Any(m => m.Chat.Id == admin.Id), "on by default");

            await chat.PressDataAsync(admin, spam, $"menuReport:{chat.ChatId}");
            Assert.IsFalse(GroupConfigs.For(chat.ChatId).Flag(ConfigFlag.Reports));
            var before = chat.Telegram.SentMessages.Count(m => m.Chat.Id == admin.Id);
            await chat.SendAsync(member, "@admin again", replyTo: spam);
            Assert.AreEqual(before, chat.Telegram.SentMessages.Count(m => m.Chat.Id == admin.Id), "off: nobody is told");

            await chat.PressDataAsync(admin, spam, $"menuReport:{chat.ChatId}");
            Assert.IsTrue(GroupConfigs.For(chat.ChatId).Flag(ConfigFlag.Reports));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task OpeningTheMenu_NoLongerResetsTheSettings()
        {
            // /menu regenerated every setting, the detection ones too, when the old hash had fewer than 8 fields.
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await chat.SendAsync(admin, "/maxtl 20");
            GroupConfigs.Change(chat.ChatId, ConfigChange.SetFlag(ConfigFlag.Welcome, true));
            var policy = Enforcer5.Detection.DetectionSettings.For(chat.ChatId);

            await chat.SendAsync(admin, "/menu");

            Assert.IsTrue(Enforcer5.Detection.DetectionSettings.For(chat.ChatId).SameSettingsAs(policy));
            Assert.IsTrue(GroupConfigs.For(chat.ChatId).Flag(ConfigFlag.Welcome));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ACustomWelcomeWithAGif_IsSaved_AndSent()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var gif = chat.Document(admin, "dance.mp4", new byte[] { 1 });
            gif.Document.MimeType = "video/mp4";
            await chat.PostAsync(gif);
            GroupConfigs.Change(chat.ChatId, ConfigChange.SetFlag(ConfigFlag.Welcome, true));

            await chat.SendAsync(admin, "/welcome Hi $name", replyTo: gif);
            var welcome = GroupConfigs.For(chat.ChatId).Welcome;
            Assert.AreEqual(("custom", "Hi $name", BanHistory.Edition), (welcome.Type, welcome.Content, welcome.MediaFileEdition));
            Assert.IsNotNull(welcome.MediaFileId);

            await chat.JoinAsync(chat.Add("Newcomer", MemberState.Left));
            Assert.IsTrue(chat.Telegram.Accepted.Any(c => c.Method == "sendDocument" && c.ChatId == chat.ChatId), "the GIF with the text");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheLogChannel_IsSetAndRemoved()
        {
            await using var chat = await HarnessChat.StartAsync();
            var creator = chat.Add("Creator", MemberState.Creator);

            await chat.SendAsync(creator, "/addlogchannel -100555");
            Assert.AreEqual(-100555L, Service.LogChannelOf(chat.ChatId));
            Assert.AreEqual(-100555L, await Scalar<long>(chat,
                "SELECT c.telegram_chat_id FROM group_settings s JOIN telegram_chats c ON c.chat_id = s.log_chat_id"));

            await chat.SendAsync(creator, "/dellogchannel");
            Assert.IsNull(Service.LogChannelOf(chat.ChatId));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task WhilePostgresIsDown_AChangeIsQueued_CountsAtOnce_AndIsWrittenOnceItIsBack()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            await chat.SendAsync(member, "hi"); // the group's config imported

            Enforcer5.Storage.PostgresStorage.UseForTests(null);
            try
            {
                await chat.SendAsync(admin, "/setabout We play werewolf");
                Assert.AreEqual(1, GroupConfigs.QueuedCount);
                await chat.SendAsync(member, "/about");
                StringAssert.Contains(chat.Replies.Last(), "We play werewolf", "counts at once");
            }
            finally
            {
                Enforcer5.Storage.PostgresStorage.UseForTests(chat.Database);
            }
            Assert.AreEqual(0L, await Scalar<long>(chat, "SELECT count(*) FROM group_content_items"));

            await GroupConfigs.DrainAsync();

            Assert.AreEqual(0, GroupConfigs.QueuedCount);
            Assert.AreEqual("We play werewolf", await Scalar<string>(chat, "SELECT content FROM group_content_items WHERE content_type = 'about'"));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheOtherEditionsChange_Arrives()
        {
            await using var chat = await HarnessChat.StartAsync();
            var member = chat.Add("Member");
            await chat.SendAsync(member, "hi");

            // The other edition changes the language and bumps the version in Redis.
            var version = await GroupConfigs.Repository.ApplyAsync(chat.ChatId, ConfigChange.SetContent(ContentKind.Language, "Deutsch"));
            chat.Keys[GroupConfigs.VersionKey(chat.ChatId)] = version.ToString();
            await chat.AdvanceAsync(GroupConfigs.RecheckAfter + TimeSpan.FromSeconds(1));
            await chat.SendAsync(member, "hello");
            await chat.SettleAsync();

            Assert.AreEqual("Deutsch", GroupConfigs.For(chat.ChatId).Text(ContentKind.Language));
            AssertNoErrors(chat);
        }
    }
}
