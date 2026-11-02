using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Detection;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Requests;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5.Tests.Harness
{
    /// <summary>
    /// A setting's name in a two-column menu explains it in a popup and changes nothing; its value changes it
    /// (Daniel, 2026-11-03). Needs the test database.
    /// </summary>
    [TestClass]
    [TestCategory("Harness")]
    [DoNotParallelize] // the harness takes over Bot.Api, Redis.db and PostgresStorage
    public class MenuInfoScenarios
    {
        private static void AssertNoErrors(HarnessChat chat) =>
            Assert.AreEqual(0, chat.Errors.Count, "Errors reported:\n" + string.Join("\n", chat.Errors));

        private static Message Shown(HarnessChat chat, HarnessUser admin, InlineKeyboardMarkup markup) => new()
        {
            Id = chat.Telegram.NextMessageId(),
            Chat = new Chat { Id = admin.Id, Type = ChatType.Private },
            From = HarnessChat.BotUser,
            Text = "Settings",
            ReplyMarkup = markup
        };

        private static AnswerCallbackQueryRequest LastAnswer(HarnessChat chat) =>
            (AnswerCallbackQueryRequest)chat.Telegram.Accepted.Last(c => c.Method == "answerCallbackQuery").Request;

        /// <summary>The row whose name is <paramref name="label"/>: its name and its value.</summary>
        private static (InlineKeyboardButton Name, InlineKeyboardButton Value) Row(InlineKeyboardMarkup menu, string label)
        {
            var row = menu.InlineKeyboard.Select(r => r.ToArray()).Single(r => r[0].Text == label);
            return (row[0], row[1]);
        }

        [TestMethod]
        public async Task TappingAFiltersName_ExplainsIt_AndChangesNothing_ItsValueStillChangesIt()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await chat.SendAsync(admin, "hi"); // the group's settings are imported
            var lang = Methods.GetGroupLanguage(chat.ChatId).Doc;
            var menu = Commands.GenFiltersMenu(chat.ChatId, lang);
            var bots = Row(menu, "Skip messages via group's bots");
            var before = DetectionSettings.For(chat.ChatId).Filters.ExemptGroupBots;
            var phone = DetectionSettings.For(chat.ChatId).Filters.PresetAction("phone");

            await chat.PressDataAsync(admin, Shown(chat, admin, menu), bots.Name.CallbackData);

            var answer = LastAnswer(chat);
            Assert.IsTrue(answer.ShowAlert);
            StringAssert.StartsWith(answer.Text, "Skip messages via group's bots: On does not filter the text");
            Assert.AreEqual(before, DetectionSettings.For(chat.ChatId).Filters.ExemptGroupBots, "the name changes nothing");

            await chat.PressDataAsync(admin, Shown(chat, admin, menu), Row(menu, "Phone numbers").Name.CallbackData);
            StringAssert.StartsWith(LastAnswer(chat).Text, "Phone numbers: Messages with a phone number");
            Assert.AreEqual(phone, DetectionSettings.For(chat.ChatId).Filters.PresetAction("phone"), "the preset is as it was");

            await chat.AdvanceAsync(System.TimeSpan.FromSeconds(20)); // the command limiter
            await chat.PressDataAsync(admin, Shown(chat, admin, menu), bots.Value.CallbackData);
            Assert.AreNotEqual(before, DetectionSettings.For(chat.ChatId).Filters.ExemptGroupBots, "the value changes it");
            AssertNoErrors(chat);
        }

        [TestMethod]
        public async Task RowsSharingAnExplanation_AreNamedAsTapped_AndMadeUpDataExplainsNothing()
        {
            await using var chat = await HarnessChat.StartAsync();
            var admin = chat.Add("Admin", MemberState.Administrator);
            await chat.SendAsync(admin, "hi");
            var lang = Methods.GetGroupLanguage(chat.ChatId).Doc;
            var media = Commands.genMediaMenu(chat.ChatId, lang);

            await chat.PressDataAsync(admin, Shown(chat, admin, media), Row(media, "Sticker").Name.CallbackData);
            Assert.AreEqual("Sticker: ✅ allowed. ❌ deleted, and the member gets a media warn; at the limit below they get the action shown there.",
                LastAnswer(chat).Text);

            await chat.PressDataAsync(admin, Shown(chat, admin, media), $"menuinfo:{chat.ChatId}:Report");
            Assert.AreEqual("This switch is no longer used and changes nothing.", LastAnswer(chat).Text, "not on the menu: no name");
            await chat.PressDataAsync(admin, Shown(chat, admin, media), $"menuinfo:{chat.ChatId}:../hcommandban");
            Assert.IsFalse(LastAnswer(chat).ShowAlert, "only explanations are shown");
            await chat.PressDataAsync(admin, Shown(chat, admin, media), $"menuinfo:{chat.ChatId}:nothing");
            Assert.IsFalse(LastAnswer(chat).ShowAlert);
            AssertNoErrors(chat);
        }
    }
}
