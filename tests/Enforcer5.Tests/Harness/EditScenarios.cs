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
    /// approved members are left out; the report says what changed and when. A deleted post is quoted, not
    /// linked to, and its file goes to an admin who asks for it, in private. Needs the test database.
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

        private static Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton[] Buttons(Message message) =>
            message.ReplyMarkup?.InlineKeyboard.SelectMany(r => r).ToArray() ?? Array.Empty<Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton>();

        private static string Link(HarnessChat chat, Message message) => Methods.GetChatMessageLink(chat.ChatId, message.MessageId.ToString());

        private static string LastAnswer(HarnessChat chat) => chat.Telegram.Accepted.Last(c => c.Method == "answerCallbackQuery").Text;

        private static Message Audio(HarnessChat chat, HarnessUser from) => new Message
        {
            Id = chat.Telegram.NextMessageId(),
            From = from.ToUser(),
            Audio = new Audio { FileId = "audio-file", FileUniqueId = "audio-unique", Duration = 42, Title = "Song", Performer = "Band", FileSize = 3_250_000 },
            Caption = "listen <now>"
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
            StringAssert.Contains(Reports(chat, admin).Last().Text, "edited long after posting</a> (11 min after posting)");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AnOldPost_EditedWeeksLater_WithNoRecord_IsNothing()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = Established(chat, "Member");
            var old = chat.Photo(member, "old");
            old.Date = chat.Clock.UtcNow.AddDays(-40);
            chat.Telegram.Posted(chat.ChatId, old.MessageId, member.Id);

            await chat.EditAsync(old, chat.Photo(member, "other"));

            Assert.AreEqual(0, Reports(chat, admin).Length, "members cannot edit after 48 hours: no record, nothing to compare");
            Assert.AreEqual(0, (await Detections(chat)).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ATagChange_ResendingAMembersPostsUnchanged_IsNothing_ButARealEditStillIs()
        {
            // 2026-10-29: a bot changed three members' tags twice; Telegram sent an edit of each of their posts
            // of the last hours, with a new edit date and nothing changed, and 70 posts were reported as late edits.
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = Established(chat, "Member");
            var newcomer = chat.Add("Newcomer");
            var posts = new System.Collections.Generic.List<Message>();
            for (var i = 0; i < 20; i++)
            {
                posts.Add(await chat.SendAsync(member, $"message {i}"));
                await chat.AdvanceAsync(TimeSpan.FromMinutes(10));
            }
            var photo = await chat.PostAsync(chat.Photo(newcomer, "p", "look"));
            await chat.AdvanceAsync(TimeSpan.FromHours(3));

            foreach (var post in posts) await chat.EditAsync(post, chat.Text(member, post.Text));
            await chat.EditAsync(photo, chat.Photo(newcomer, "p", "look"));

            Assert.AreEqual(0, Reports(chat, admin).Length);
            Assert.IsFalse(Deleted(chat, newcomer, photo));
            Assert.AreEqual(0, (await Detections(chat)).Length);

            await chat.EditAsync(posts[3], chat.Text(member, "message 3, edited"));
            StringAssert.Contains(Reports(chat, admin).Single().Text, "edited long after posting");
            await chat.EditAsync(posts[3], chat.Text(member, "message 3, edited"));
            Assert.AreEqual(1, (await Detections(chat)).Length, "the same edit sent again is compared with the edit, not the post");
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
        public async Task AnEditByAnInlineBot_IsLeftOut_OnlyWhenTheGroupTrustsTheBot()
        {
            // #26: a whisper bot replaces its placeholder text; the bot made the edit, not the member.
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = Established(chat, "Member");
            var trusted = new User { Id = 7_000_001, IsBot = true, FirstName = "Whisper", Username = "whisperbot" };
            var untrusted = new User { Id = 7_000_002, IsBot = true, FirstName = "Rewriter", Username = "rewriterbot" };
            var game = new User { Id = 7_000_003, IsBot = true, FirstName = "Werewolf", Username = "werewolfbot" };
            await BotGuard.TrustAsync(chat.ChatId, trusted.Id, admin.Id); // what /trustbot does
            await chat.CompleteAsync();

            async Task<Message> Via(User bot)
            {
                var post = chat.Text(member, "a whisper for someone");
                post.ViaBot = bot;
                return await chat.PostAsync(post);
            }
            var viaTrusted = await Via(trusted);
            var viaUntrusted = await Via(untrusted);
            var viaGame = await Via(game);
            await chat.AdvanceAsync(TimeSpan.FromMinutes(11));
            foreach (var post in new[] { viaTrusted, viaUntrusted, viaGame })
            {
                var edit = chat.Text(member, "someone read the whisper");
                edit.ViaBot = post.ViaBot;
                await chat.EditAsync(post, edit);
            }

            Assert.AreEqual(1, (await Detections(chat)).Length, "only the untrusted bot's edit is the member's");
            Assert.AreEqual(1, Reports(chat, admin).Length);
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

        [TestMethod]
        public async Task ASwapIsDeleted_SoTheReportQuotesIt_AndShowsTheFileOnlyToTheAdminWhoAsks()
        {
            // 2026-11-03: "Go to message" under a swap's report went nowhere: the bot had deleted the post.
            await using var chat = await HarnessChat.StartAsync();
            chat.Telegram.ParsesHtml = true;
            var admin = chat.Add("Admin", MemberState.Administrator);
            var other = chat.Add("Other admin", MemberState.Administrator);
            var member = Established(chat, "Member"); // no newcomer signs on the photo: only the swap
            await chat.PostAsync(chat.Photo(member, "first")); // recording starts here
            var text = await chat.SendAsync(member, "hello");

            await chat.EditAsync(text, Audio(chat, member));

            Assert.IsTrue(Deleted(chat, member, text));
            var report = Reports(chat, admin).Single();
            Assert.IsFalse(report.Text.Contains(Link(chat, text)), "no link to the deleted post");
            Assert.IsFalse(Buttons(report).Any(b => b.Url != null), "no Go to message");
            StringAssert.Contains(report.Text, "Deleted post");
            StringAssert.Contains(report.Text, "<blockquote expandable>audio · 0:42 · Song – Band · 3.1 MB\nlisten &lt;now&gt;</blockquote>");

            chat.Telegram.SetNotStarted(other.Id);
            var show = Buttons(report).Single(b => b.CallbackData?.EndsWith(":show") == true).CallbackData;
            await chat.PressDataAsync(member, report, show);
            Assert.AreEqual(0, chat.Telegram.ResentFiles.Count, "only the group's admins");
            await chat.PressDataAsync(other, report, show);
            Assert.AreEqual("Start me in private first, then press again.", LastAnswer(chat));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(15));
            await chat.PressDataAsync(admin, report, show);
            Assert.AreEqual("Sent to you in private. It cannot be forwarded or saved.", LastAnswer(chat));
            Assert.AreEqual(new FakeTelegram.ResentFile(admin.Id, "sendAudio", "audio-file", true), chat.Telegram.ResentFiles.Single());
            Assert.IsFalse(chat.Telegram.Accepted.Any(c => c.ChatId == chat.ChatId && c.Method == "sendAudio"), "never in the group");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AfterARestart_TheReportStillQuotesTheDeletedPost_AndShowStillWorks()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = Established(chat, "Member");
            var photo = await chat.PostAsync(chat.Photo(member, "cat"));
            await chat.EditAsync(photo, Sticker(chat, member, "bad"));
            var report = Reports(chat, admin).Single();
            await DetectionRecorder.FlushAsync();

            await chat.CrashAndRestartAsync();
            var post = await chat.SendAsync(member, "still here");
            await chat.AdvanceAsync(TimeSpan.FromMinutes(11));
            await chat.EditAsync(post, chat.Text(member, "still here, edited"));
            await chat.AdvanceAsync(TimeSpan.FromSeconds(31));

            var edit = chat.Telegram.Accepted.Last(c => c.Method == "editMessageText" && c.ChatId == admin.Id);
            StringAssert.Contains(edit.Text, "<blockquote expandable>sticker</blockquote>");
            StringAssert.Contains(edit.Text, $"<a href=\"{Link(chat, post)}\">edited long after posting</a>", "the late edit was not deleted");
            Assert.IsFalse(edit.Text.Contains(Link(chat, photo)));
            var buttons = ((Telegram.Bot.Requests.EditMessageTextRequest)edit.Request).ReplyMarkup.InlineKeyboard.SelectMany(r => r).ToArray();
            Assert.AreEqual(Link(chat, post), buttons.Single(b => b.Url != null).Url, "Go to message: the post still there");

            await chat.CrashAndRestartAsync();
            await chat.PressDataAsync(admin, report, buttons.Single(b => b.CallbackData?.EndsWith(":show") == true).CallbackData);
            Assert.AreEqual(new FakeTelegram.ResentFile(admin.Id, "sendSticker", "sbad", true), chat.Telegram.ResentFiles.Single());
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ALateEdit_DeletedForANewMember_IsQuoted_WithoutAShowButton()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var newcomer = chat.Add("Newcomer");
            var post = await chat.SendAsync(newcomer, "hi");
            await chat.AdvanceAsync(TimeSpan.FromMinutes(11));

            await chat.EditAsync(post, chat.Text(newcomer, "buy cheap followers"));

            var report = Reports(chat, admin).Single();
            StringAssert.Contains(report.Text, "<blockquote expandable>buy cheap followers</blockquote>");
            Assert.IsFalse(report.Text.Contains(Link(chat, post)));
            Assert.IsFalse(Buttons(report).Any(b => b.Url != null || b.CallbackData?.EndsWith(":show") == true));
            AssertNoErrors(chat);
        }

        // ------------------------------------------------------------------ an inline bot's swap (Daniel, 2026-11-04)

        private static readonly User MusicBot = new() { Id = 7_000_010, IsBot = true, FirstName = "Music", Username = "musicbot" };

        /// <summary>A music bot's inline post: a placeholder text, which the bot edits into the song.</summary>
        private static async Task<Message> SongThroughTheBot(HarnessChat chat, HarnessUser member)
        {
            var placeholder = chat.Text(member, "Loading the song…");
            placeholder.ViaBot = MusicBot;
            var post = await chat.PostAsync(placeholder);
            await chat.AdvanceAsync(TimeSpan.FromMinutes(1));
            var song = Audio(chat, member);
            song.ViaBot = MusicBot;
            await chat.EditAsync(post, song);
            return post;
        }

        private static async Task<(string Status, string Source)[]> GroupBotRows(HarnessChat chat)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return (await connection.QueryAsync<(string, string)>("SELECT status, source FROM group_bots")).ToArray();
        }

        [TestMethod]
        public async Task AnInlineBotsSwap_NamesTheBot_AndTrustUnderTheReport_TrustsItInThisGroupOnly_AndMarksTheMemberFine()
        {
            await using var chat = await HarnessChat.StartAsync();
            chat.Telegram.ParsesHtml = true;
            chat.Keys.AddToSet("premiumBot", chat.ChatId.ToString()); // even in a premium group: this group only
            await BotGuard.ReloadAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = Established(chat, "Member");
            await chat.PostAsync(chat.Photo(member, "first")); // recording starts here

            var song = await SongThroughTheBot(chat, member);

            Assert.IsTrue(Deleted(chat, member, song), "an untrusted bot's swap is the member's");
            var report = Reports(chat, admin).Single();
            StringAssert.Contains(report.Text, "text replaced by audio");
            StringAssert.Contains(report.Text, "🤖 Sent through the inline bot <a href=\"https://t.me/musicbot\">@musicbot</a>.");
            var trust = Buttons(report).Single(b => b.CallbackData?.EndsWith(":trustbot") == true);
            Assert.AreEqual("🤖 Trust @musicbot", trust.Text);
            Assert.IsTrue(trust.CallbackData.Length <= 64, trust.CallbackData);

            await chat.PressDataAsync(member, report, trust.CallbackData);
            Assert.AreEqual(0, (await GroupBotRows(chat)).Length, "only the group's admins");

            await chat.PressDataAsync(admin, report, trust.CallbackData);
            Assert.AreEqual("@musicbot is trusted in this group now. Posts sent through it are no longer checked here.", LastAnswer(chat));
            CollectionAssert.AreEqual(new[] { ("allowed", "report") }, await GroupBotRows(chat));
            Assert.IsFalse(BotGuard.IsTrustedEverywhere(MusicBot.Id), "one tap on a report never trusts a bot in every group");
            var edit = chat.Telegram.Accepted.Last(c => c.Method == "editMessageText" && c.ChatId == admin.Id);
            StringAssert.Contains(edit.Text, "which this group trusts now");
            StringAssert.Contains(edit.Text, "Marked fine by");
            var buttons = ((Telegram.Bot.Requests.EditMessageTextRequest)edit.Request).ReplyMarkup?.InlineKeyboard.SelectMany(r => r).ToArray()
                          ?? Array.Empty<Telegram.Bot.Types.ReplyMarkups.InlineKeyboardButton>();
            Assert.IsFalse(buttons.Any(b => b.CallbackData?.EndsWith(":trustbot") == true), "nothing left to trust");

            // The next song through it is the bot's doing, and left alone, for any member.
            var newcomer = chat.Add("Newcomer");
            await chat.PostAsync(chat.Photo(newcomer, "hello"));
            var next = await SongThroughTheBot(chat, newcomer);
            Assert.IsFalse(Deleted(chat, newcomer, next));
            Assert.AreEqual(1, (await Detections(chat)).Length);

            await chat.SendAsync(admin, "/trustedbots");
            StringAssert.Contains(chat.Replies.Last(), "trusted by an admin from a Detection report");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AfterARestart_TheReportStillNamesTheBot_AndTrustStillWorks()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = Established(chat, "Member");
            await chat.PostAsync(chat.Photo(member, "first"));
            await SongThroughTheBot(chat, member);
            var report = Reports(chat, admin).Single();
            var trust = Buttons(report).Single(b => b.CallbackData?.EndsWith(":trustbot") == true).CallbackData;
            await DetectionRecorder.FlushAsync();

            await chat.CrashAndRestartAsync();
            await chat.PressDataAsync(admin, report, trust);

            Assert.AreEqual("@musicbot is trusted in this group now. Posts sent through it are no longer checked here.", LastAnswer(chat));
            CollectionAssert.AreEqual(new[] { ("allowed", "report") }, await GroupBotRows(chat));
            StringAssert.Contains(chat.Telegram.Accepted.Last(c => c.Method == "editMessageText" && c.ChatId == admin.Id).Text,
                "@musicbot</a>, which this group trusts now");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ABotTheAdminsRemoved_IsNamed_ButNeverTrustedFromAReport()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = Established(chat, "Member");
            await chat.PostAsync(chat.Photo(member, "first"));
            await SongThroughTheBot(chat, member);
            var report = Reports(chat, admin).Single();
            var trust = Buttons(report).Single(b => b.CallbackData?.EndsWith(":trustbot") == true).CallbackData;

            await BotGuard.UntrustAsync(chat.ChatId, MusicBot.Id, admin.Id); // what /untrustbot does
            await chat.CompleteAsync();
            await chat.AdvanceAsync(TimeSpan.FromSeconds(15));
            await chat.PressDataAsync(admin, report, trust);

            StringAssert.StartsWith(LastAnswer(chat), "@musicbot was removed from this group");
            CollectionAssert.AreEqual(new[] { ("blocked", "command") }, await GroupBotRows(chat));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TrustBot_InReplyToAPostSentThroughAnInlineBot_TrustsTheBot_NotTheMember()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = Established(chat, "Member");
            var post = chat.Text(member, "a song");
            post.ViaBot = MusicBot;
            post = await chat.PostAsync(post);

            await chat.SendAsync(admin, "/trustbot", post);

            CollectionAssert.AreEqual(new[] { ("allowed", "command") }, await GroupBotRows(chat));
            Assert.IsTrue(await BotGuard.IsTrustedHereAsync(chat.ChatId, MusicBot));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public void WithViaBot_AddsTheBotToTheEvidence_AndKeepsWhatWasThere()
        {
            Assert.IsNull(DetectionEngine.WithViaBot(null, null));
            Assert.AreEqual("{\"a\":1}", DetectionEngine.WithViaBot("{\"a\":1}", null));
            Assert.AreEqual("{\"a\":1,\"via_bot\":7000010,\"via_bot_username\":\"musicbot\"}", DetectionEngine.WithViaBot("{\"a\":1}", MusicBot));
            Assert.AreEqual("{\"via_bot\":5}", DetectionEngine.WithViaBot(null, new User { Id = 5, IsBot = true }));
        }
    }
}
