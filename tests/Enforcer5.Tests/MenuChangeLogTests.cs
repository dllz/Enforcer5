using Enforcer5;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types.ReplyMarkups;

namespace Enforcer5.Tests.Logging
{
    /// <summary>Reading what a menu button changed off the keyboard before and after the press.</summary>
    [TestClass]
    public class MenuChangeLogTests
    {
        private static InlineKeyboardButton B(string text, string data) => InlineKeyboardButton.WithCallbackData(text, data);

        private static InlineKeyboardMarkup Keys(params InlineKeyboardButton[][] rows) => new InlineKeyboardMarkup(rows);

        [TestMethod]
        public void ALabelledToggle_IsNamedByItsLabel()
        {
            var before = Keys(new[] { B("Late edits", "t:late"), B("On", "t:late") }, new[] { B("Back", "back") });
            var after = Keys(new[] { B("Late edits", "t:late"), B("Off", "t:late") }, new[] { B("Back", "back") });
            Assert.AreEqual(new MenuChangeLog.Result(MenuChangeLog.Kind.Changed, "Late edits", "On", "Off"),
                MenuChangeLog.Read(before, after, "t:late", "Edits"));
            var withOptions = Keys(new[] { B("Late edits", "t:late"), B("On", "t:late") }, new[] { B("Delete", "t:del"), B("On", "t:del") },
                new[] { B("Back", "back") });
            Assert.AreEqual(new MenuChangeLog.Result(MenuChangeLog.Kind.Changed, "Late edits", "On", "Off"),
                MenuChangeLog.Read(withOptions, after, "t:late", "Edits"), "turning it off hides its options");
        }

        [TestMethod]
        public void AStepperOrAButtonWithoutLabel_IsNamedByTheScreen()
        {
            var before = Keys(new[] { B("✅ | On", "status") }, new[] { B("➖", "dim"), B("5", "open"), B("➕", "raise") });
            Assert.AreEqual(new MenuChangeLog.Result(MenuChangeLog.Kind.Changed, "Flood", "5", "4"),
                MenuChangeLog.Read(before, Keys(new[] { B("✅ | On", "status") }, new[] { B("➖", "dim"), B("4", "open"), B("➕", "raise") }), "dim", "Flood"));
            Assert.AreEqual(new MenuChangeLog.Result(MenuChangeLog.Kind.Changed, "Flood", "✅ | On", "❌ | Off"),
                MenuChangeLog.Read(before, Keys(new[] { B("❌ | Off", "status") }, new[] { B("➖", "dim"), B("5", "open"), B("➕", "raise") }), "status", "Flood"));
        }

        [TestMethod]
        public void AnythingElse_IsAPress_AndAButtonNotOnTheKeyboard_IsUnknown()
        {
            var before = Keys(new[] { B("Go live now", "mode:live"), B("Watch 7 more days", "mode:extend") });
            var after = Keys(new[] { B("Watch only again", "mode:observe") });
            Assert.AreEqual(new MenuChangeLog.Result(MenuChangeLog.Kind.Pressed, "Go live now"), MenuChangeLog.Read(before, after, "mode:live", "Detection"));
            Assert.AreEqual(MenuChangeLog.Kind.Pressed, MenuChangeLog.Read(before, null, "mode:live", "Detection").Kind, "no redraw");
            Assert.AreEqual(MenuChangeLog.Kind.Pressed, MenuChangeLog.Read(before, before, "mode:live", "Detection").Kind, "nothing changed");
            Assert.AreEqual(MenuChangeLog.Kind.Unknown, MenuChangeLog.Read(before, after, "other", "Detection").Kind);
            Assert.AreEqual(MenuChangeLog.Kind.Unknown, MenuChangeLog.Read(null, after, "mode:live", "Detection").Kind);
        }

        [TestMethod]
        public void TheTitle_IsTheFirstLine_Shortened()
        {
            Assert.AreEqual("Edits", MenuChangeLog.Title("\n Edits \nmore text"));
            Assert.AreEqual(61, MenuChangeLog.Title(new string('x', 100)).Length);
            Assert.AreEqual("", MenuChangeLog.Title(null));
        }
    }
}
