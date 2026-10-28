using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Detection;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Detection step 8: the edit rules. Swapped media and text turned into media are reported and deleted,
    /// always; late edits by default are deleted for new members and reported for everyone; admins and
    /// approved members are left out; the report says what changed and when, never the content. Needs the
    /// test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Detection")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class EditScenarios
    {
        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static Message[] Reports(HarnessChat chat, HarnessUser admin) =>
            chat.Telegram.SentMessages.Where(m => m.Chat.Id == admin.Id && m.Text?.StartsWith("🛡") == true).ToArray();

        private static bool Deleted(HarnessChat chat, HarnessUser user, Message message) =>
            !chat.Telegram.MessagesFrom(chat.ChatId, user.Id).Contains(message.MessageId);

        private static Message Sticker(HarnessChat chat, HarnessUser from, string uniqueId) => new Message
        {
            Id = chat.Telegram.NextMessageId(),
            From = from.ToUser(),
            Sticker = new Sticker { FileId = "s" + uniqueId, FileUniqueId = uniqueId, Width = 1, Height = 1 }
        };

        private static async Task<(string Sign, string Mode, bool Deleted, bool Reported)[]> Detections(HarnessChat chat)
        {
            await DetectionRecorder.FlushAsync();
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<(string, string, bool, bool)>(
                "SELECT sign, mode, deleted, reported FROM detections WHERE sign LIKE 'edit_%' ORDER BY detection_id")).ToArray();
        }

        private static HarnessUser Established(HarnessChat chat, string name)
        {
            var user = chat.Add(name);
            chat.Hashes[$"{chat.ChatId}:users:{user.Id}", "msgs"] = "5000";
            return user;
        }

        [TestMethod]
        public async Task APhotoSwappedForASticker_IsDeletedAndReported_EvenFromAnEstablishedMember_WhileWatching()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = Established(chat, "Member");
            var photo = await chat.PostAsync(chat.Photo(member, "cat"));

            await chat.EditAsync(photo, Sticker(chat, member, "bad"));

            Assert.IsTrue(Deleted(chat, member, photo));
            var report = Reports(chat, admin).Single();
            StringAssert.Contains(report.Text, "edited into different media");
            StringAssert.Contains(report.Text, "photo replaced by sticker, 1 min after posting");
            CollectionAssert.AreEqual(new[] { ("edit_swap", "live", true, true) }, await Detections(chat));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TextTurnedIntoMedia_IsASwap_OnlyForPostsMadeSinceRecordingStarted()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            var before = await chat.SendAsync(member, "posted before any media was recorded");
            await chat.AdvanceAsync(TimeSpan.FromSeconds(5));
            await chat.PostAsync(chat.Photo(member, "first")); // recording starts here
            await chat.AdvanceAsync(TimeSpan.FromSeconds(5));
            var text = await chat.SendAsync(member, "hello");

            await chat.EditAsync(before, chat.Photo(member, "x"));
            Assert.IsFalse(Deleted(chat, member, before), "may have been media we never saw");
            await chat.EditAsync(text, chat.Photo(member, "y"));

            Assert.IsTrue(Deleted(chat, member, text));
            StringAssert.Contains(Reports(chat, admin).Single().Text, "text replaced by photo");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ACaptionFixedInTime_IsNothing_ButLate_DeletesForANewMember_AndOnlyReportsAnEstablishedOne()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var newcomer = chat.Add("Newcomer");
            var regular = Established(chat, "Regular");
            var quick = await chat.PostAsync(chat.Photo(newcomer, "a", "typo"));
            await chat.AdvanceAsync(TimeSpan.FromMinutes(2));
            await chat.EditAsync(quick, chat.Photo(newcomer, "a", "fixed"));
            Assert.AreEqual(0, Reports(chat, admin).Length);

            var newPost = await chat.SendAsync(newcomer, "hi");
            var regularPost = await chat.SendAsync(regular, "hi");
            await chat.AdvanceAsync(TimeSpan.FromMinutes(11));
            await chat.EditAsync(newPost, chat.Text(newcomer, "hi there"));
            await chat.EditAsync(regularPost, chat.Text(regular, "hi there"));

            Assert.IsTrue(Deleted(chat, newcomer, newPost));
            Assert.IsFalse(Deleted(chat, regular, regularPost));
            Assert.AreEqual(2, Reports(chat, admin).Length);
            StringAssert.Contains(Reports(chat, admin).Last().Text, "edited long after posting (11 min after posting)");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnOldPost_EditedWeeksLater_IsLate_AndTheReportSaysHowLongAfter()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = Established(chat, "Member");
            var old = chat.Photo(member, "old");
            old.Date = chat.Clock.UtcNow.AddDays(-40);
            chat.Telegram.Posted(chat.ChatId, old.MessageId, member.Id);

            await chat.EditAsync(old, chat.Photo(member, "other"));

            StringAssert.Contains(Reports(chat, admin).Single().Text, "40 days after posting");
            CollectionAssert.AreEqual(new[] { ("edit_late", "live", false, true) }, await Detections(chat), "no record that old: late, not a swap");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AdminsAndApprovedMembers_AreLeftOut()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var approved = chat.Add("Approved");
            chat.Keys.AddToSet($"chat:{chat.ChatId}:watch", approved.Id.ToString());
            var adminPhoto = await chat.PostAsync(chat.Photo(admin, "a"));
            var approvedPhoto = await chat.PostAsync(chat.Photo(approved, "b"));

            await chat.EditAsync(adminPhoto, Sticker(chat, admin, "c"));
            await chat.EditAsync(approvedPhoto, Sticker(chat, approved, "d"));

            Assert.AreEqual(0, Reports(chat, admin).Length);
            Assert.AreEqual(0, (await Detections(chat)).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheEditsMenu_TurnsLateEditsOff_ForEstablishedMembers()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var regular = Established(chat, "Regular");
            await chat.SendAsync(admin, "/menu");
            var menu = chat.Telegram.SentMessages.Last(m => m.Chat.Id == admin.Id);
            await chat.PressDataAsync(admin, menu, $"editsmenu:{chat.ChatId}");
            await chat.AdvanceAsync(TimeSpan.FromSeconds(15));
            await chat.PressDataAsync(admin, menu, $"editstoggle:{chat.ChatId}:established");
            Assert.IsTrue((await new Enforcer5.Data.PostgresDetectionSettingsRepository(() => chat.Database).LoadAsync(chat.ChatId))
                .Engine.For("edit_late").SkipEstablished);

            var post = await chat.SendAsync(regular, "hi");
            await chat.AdvanceAsync(TimeSpan.FromMinutes(11));
            await chat.EditAsync(post, chat.Text(regular, "hi there"));

            Assert.AreEqual(0, Reports(chat, admin).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnEditedMessage_GoesThroughTheFilters()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await chat.ChangeSettingsAsync(p => p with { Filters = p.Filters.WithPreset("invite_link", "delete") });
            var member = chat.Add("Member");
            var post = await chat.SendAsync(member, "hello");

            await chat.EditAsync(post, chat.Text(member, "join t.me/+abcdefghijk"));

            Assert.IsTrue(Deleted(chat, member, post));
            AssertNoErrors(chat);
        }
    }
}
