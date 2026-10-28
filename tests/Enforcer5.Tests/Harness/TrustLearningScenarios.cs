using System;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Data;
using Enforcer5.Detection;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// Detection step 7b: the established level and the report level learned from the admins' decisions,
    /// told to the admins with Undo and Keep; /untrust and /trust; the nightly easing back, report level and
    /// question about restoring a member. Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [TestCategory("Detection")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class TrustLearningScenarios
    {
        private static readonly SignDefinition Scored = SignCatalog.AddForTests("test_learn_scored", 4, "score");

        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static string S(HarnessChat chat, string key, params object[] args) =>
            Methods.GetLocaleString(Methods.GetGroupLanguage(chat.ChatId).Doc, key, args);

        private static Message[] Learned(HarnessChat chat, HarnessUser admin) =>
            chat.Telegram.SentMessages.Where(m => m.Chat.Id == admin.Id && m.Text?.StartsWith("🧠") == true).ToArray();

        private static InlineKeyboardButton[] Buttons(Message message) =>
            message.ReplyMarkup?.InlineKeyboard.SelectMany(r => r).ToArray() ?? Array.Empty<InlineKeyboardButton>();

        private static async Task<EngineSettings> Engine(HarnessChat chat) =>
            (await new PostgresDetectionSettingsRepository(() => chat.Database).LoadAsync(chat.ChatId)).Engine;

        private static Task GoLive(HarnessChat chat) =>
            chat.ChangeSettingsAsync(p => p with { Engine = p.Engine with { ObserveUntil = chat.Clock.GetUtcNow().AddMinutes(-1) } });

        private static HarnessUser Established(HarnessChat chat, string name, int messages)
        {
            var user = chat.Add(name);
            chat.Hashes[$"{chat.ChatId}:users:{user.Id}", "msgs"] = messages.ToString();
            return user;
        }

        /// <summary>A sign Detection records for anyone (a blocked inline bot), about <paramref name="message"/>.</summary>
        private static async Task Sign(HarnessChat chat, HarnessUser user, Message message)
        {
            DetectionEngine.Checked(chat.ChatId, user.Id, message, SignCatalog.InlineBot, "delete", deleted: true);
            await chat.SettleAsync();
        }

        private static async Task<int> Adjustments(HarnessChat chat, string reason)
        {
            await using var connection = await chat.Database.OpenConnectionAsync();
            return await connection.ExecuteScalarAsync<int>("SELECT count(*) FROM detection_adjustments WHERE reason = @Reason", new { Reason = reason });
        }

        [TestMethod]
        public async Task BanningAnEstablishedMemberAfterASign_RaisesTheLevel_TellsTheAdmins_AndUndoPutsItBack()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var spammer = Established(chat, "Spammer", 25);
            var regular = Established(chat, "Regular", 22);
            Assert.AreEqual(TrustLevel.Established, await MemberTrust.OfAsync(chat.ChatId, regular.Id));
            var spam = await chat.SendAsync(spammer, "buy now");
            await Sign(chat, spammer, spam);

            await chat.SendAsync(admin, "/ban", replyTo: spam);
            await chat.SettleAsync();

            var engine = await Engine(chat);
            var messages = int.Parse(chat.Hashes[$"{chat.ChatId}:users:{spammer.Id}", "msgs"]);
            var expected = 20 + (int)Math.Ceiling((messages + 1 - 20) / 2.0);
            Assert.AreEqual((expected, 7), (engine.TrustMessages, engine.TrustDays));
            Assert.AreEqual(TrustLevel.New, await MemberTrust.OfAsync(chat.ChatId, regular.Id), "below the new level");
            var notice = Learned(chat, admin).Single();
            StringAssert.Contains(notice.Text, $"{expected} messages and 7 days");
            StringAssert.Contains(notice.Text, "was 20 and 7");
            StringAssert.Contains(notice.Text, S(chat, "learnAction_ban"));
            Assert.IsFalse(notice.Text.Contains("buy now"), "never the message's text");

            await chat.PressAsync(admin, notice, $"{TrustLearning.Trigger}:{chat.ChatId}:");
            Assert.IsNull((await Engine(chat)).TrustMessages, "undone");
            Assert.AreEqual(TrustLevel.Established, await MemberTrust.OfAsync(chat.ChatId, regular.Id));
            StringAssert.Contains(chat.Telegram.Accepted.Last(c => c.Method == "editMessageText" && c.ChatId == admin.Id).Text, "Undone by");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task ABanWithoutASign_OrOfANewMember_TeachesNothing()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var arguer = Established(chat, "Arguer", 25);
            await chat.SendAsync(admin, "/ban", replyTo: await chat.SendAsync(arguer, "you are wrong"));
            var newcomer = chat.Add("Newcomer");
            var spam = await chat.SendAsync(newcomer, "buy now");
            await Sign(chat, newcomer, spam);
            await chat.SendAsync(admin, "/ban", replyTo: spam);
            await chat.SettleAsync();

            Assert.IsNull((await Engine(chat)).TrustMessages);
            Assert.AreEqual(0, Learned(chat, admin).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AVeryActiveMember_LooksTakenOver_AndTheLevelStays()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var old = Established(chat, "Old hand", 500);
            var spam = await chat.SendAsync(old, "buy now");
            await Sign(chat, old, spam);

            await chat.SendAsync(admin, "/ban", replyTo: spam);
            await chat.SettleAsync();

            Assert.IsNull((await Engine(chat)).TrustMessages);
            var notice = Learned(chat, admin).Single();
            StringAssert.Contains(notice.Text, "taken-over");
            Assert.AreEqual(0, Buttons(notice).Length, "nothing to undo");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task WhileOnlyWatching_TheRaiseIsProposed_NotApplied()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var spammer = Established(chat, "Spammer", 25);
            var spam = await chat.SendAsync(spammer, "buy now");
            await Sign(chat, spammer, spam);

            await chat.SendAsync(admin, "/ban", replyTo: spam);
            await chat.SettleAsync();

            Assert.IsNull((await Engine(chat)).TrustMessages);
            var notice = Learned(chat, admin).Single();
            StringAssert.Contains(notice.Text, "only watching");
            Assert.AreEqual(0, Buttons(notice).Length);
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Untrust_KeepsAMemberNew_RaisesTheLevel_AndTrustRestoresThem()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = Established(chat, "Member", 40);
            var other = chat.Add("Other admin", MemberState.Administrator);

            await chat.SendAsync(admin, $"/untrust {member.Id}");
            await chat.SettleAsync();

            Assert.AreEqual(S(chat, "untrustDone", member.Id.ToString()), chat.Replies.Last());
            Assert.AreEqual(TrustLevel.New, await MemberTrust.OfAsync(chat.ChatId, member.Id));
            Assert.AreEqual(31, (await Engine(chat)).TrustMessages);
            StringAssert.Contains(Learned(chat, admin).Single().Text, "set back to new");

            await chat.CrashAndRestartAsync();
            Assert.AreEqual(TrustLevel.New, await MemberTrust.OfAsync(chat.ChatId, member.Id), "kept in PostgreSQL");
            await chat.SendAsync(admin, $"/untrust {member.Id}");
            Assert.AreEqual(S(chat, "untrustAlready", member.Id.ToString()), chat.Replies.Last());
            await chat.SendAsync(admin, $"/untrust {other.Id}");
            Assert.AreEqual(S(chat, "untrustAdmin"), chat.Replies.Last());

            await chat.SendAsync(admin, $"/trust {member.Id}");
            Assert.AreEqual(S(chat, "trustDone", member.Id.ToString()), chat.Replies.Last());
            Assert.AreEqual(TrustLevel.Established, await MemberTrust.OfAsync(chat.ChatId, member.Id), "40 messages: above 31");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheUserCommand_SaysWhy_AndHasTheButton()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = Established(chat, "Member", 40);

            await chat.SendAsync(admin, $"/user {member.Id}");
            var info = chat.LastBotMessage;
            await chat.PressAsync(admin, info, "userbuttonuntrust:");

            Assert.IsTrue(TrustOverrides.IsSet(chat.ChatId, member.Id));
            await chat.SendAsync(admin, $"/user {member.Id}");
            StringAssert.Contains(chat.LastBotMessage.Text, "Set back to new by an admin");
            Assert.IsTrue(Buttons(chat.LastBotMessage).Any(b => b.CallbackData == $"userbuttontrust:{chat.ChatId}:{member.Id}"));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Nightly_LowersTheReportLevel_WhenLowScoresWereMostlySpam_OncePerDay()
        {
            await using var chat = await HarnessChat.StartAsync();
            await GoLive(chat);
            var admin = chat.Add("Admin", MemberState.Administrator);
            for (var i = 0; i < 10; i++)
            {
                var member = chat.Add($"Member{i}");
                // A member the bot has seen, so the detection is kept.
                var message = await chat.SendAsync(member, "hi");
                await DetectionEngine.HitAsync(chat.ChatId, member.Id, message, new SignHit(Scored.Name, Weight: 2));
                await DetectionRecorder.FlushAsync();
                if (i < 8) Methods.BanUser(chat.ChatId, member.Id, Methods.GetGroupLanguage(chat.ChatId).Doc, "spam", admin.Id);
                else Decisions.Fine(chat.ChatId, member.Id, admin.Id, new[] { Scored.Name });
                await chat.SettleAsync();
            }

            await TrustLearning.NightlyAsync(anyHour: true);
            await chat.SettleAsync();
            await TrustLearning.NightlyAsync(anyHour: true);
            await chat.SettleAsync();

            Assert.AreEqual(2, (await Engine(chat)).ReportAt);
            var notice = Learned(chat, admin).Single();
            StringAssert.Contains(notice.Text, "8 were removed or muted and 2 were fine");
            await chat.PressAsync(admin, notice, $"{TrustLearning.Trigger}:");
            Assert.AreEqual(3, (await Engine(chat)).ReportAt, "Undo is the first button");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task Nightly_EasesTheLevelBack_AWeekAfterTheLastRaise()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await chat.ChangeSettingsAsync(p => p with
            {
                Engine = p.Engine with { TrustMessages = 21, TrustDays = 7, TrustChangedAt = chat.Clock.GetUtcNow() }
            });

            await TrustLearning.NightlyAsync(anyHour: true);
            Assert.AreEqual(21, (await Engine(chat)).TrustMessages, "not before a week");

            await chat.AdvanceAsync(TimeSpan.FromDays(8));
            await TrustLearning.NightlyAsync(anyHour: true);
            await chat.SettleAsync();

            Assert.IsNull((await Engine(chat)).TrustMessages);
            StringAssert.Contains(Learned(chat, admin).Single().Text, "back to the usual");
            Assert.AreEqual(1, await Adjustments(chat, "decay"));
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task AMemberSetBackToNew_WhoDidWell_IsOfferedBack_AndRestoreRestoresThem()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            var member = chat.Add("Member");
            await chat.SendAsync(admin, $"/untrust {member.Id}");
            await chat.SettleAsync();

            await chat.AdvanceAsync(TimeSpan.FromDays(31));
            await TrustLearning.NightlyAsync(anyHour: true);
            Assert.AreEqual(0, Learned(chat, admin).Length, "has not talked since");

            chat.Hashes[$"{chat.ChatId}:users:{member.Id}", "msgs"] = "60";
            await chat.AdvanceAsync(TimeSpan.FromDays(1));
            await TrustLearning.NightlyAsync(anyHour: true);
            await chat.SettleAsync();

            var question = Learned(chat, admin).Single();
            StringAssert.Contains(question.Text, "Count them as established again?");
            await chat.PressAsync(admin, question, $"{TrustLearning.RestoreTrigger}:");
            Assert.IsFalse(TrustOverrides.IsSet(chat.ChatId, member.Id));

            await chat.AdvanceAsync(TimeSpan.FromDays(1));
            await TrustLearning.NightlyAsync(anyHour: true);
            Assert.AreEqual(1, Learned(chat, admin).Length, "asked once");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task TheLearnedScreen_ShowsTheLevels_AndUndoAllResetsThem()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await chat.ChangeSettingsAsync(p => p with { Engine = p.Engine with { TrustMessages = 30, TrustDays = 9, ReportAt = 4 } });
            await chat.SendAsync(admin, "/menu");
            var menu = chat.Telegram.SentMessages.Last(m => m.Chat.Id == admin.Id);

            await chat.PressDataAsync(admin, menu, $"learnmenu:{chat.ChatId}");
            var screen = chat.Telegram.Accepted.Last(c => c.Method == "editMessageText" && c.ChatId == admin.Id).Text;
            StringAssert.Contains(screen, "30 messages and 9 days");
            StringAssert.Contains(screen, "the usual here is 20 messages and 7 days");

            await chat.AdvanceAsync(TimeSpan.FromSeconds(15));
            await chat.PressDataAsync(admin, menu, $"learnreset:{chat.ChatId}");
            var engine = await Engine(chat);
            Assert.AreEqual(((int?)null, 3), (engine.TrustMessages, engine.ReportAt));
            Assert.AreEqual(2, await Adjustments(chat, "reset"));
            AssertNoErrors(chat);
        }
    }
}
