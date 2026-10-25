using System.Collections.Generic;
using System.Linq;
using Enforcer5.Helpers;
using Enforcer5.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests
{
    [TestClass]
    public class InlineKeyboardTests
    {
        private static string[][] Layout(Telegram.Bot.Types.ReplyMarkups.InlineKeyboardMarkup markup) =>
            markup.InlineKeyboard.Select(row => row.Select(b => b.Text).ToArray()).ToArray();

        [TestMethod]
        public void TwoColumns_FillRowsInOrder()
        {
            var menu = new Menu(2, new List<InlineButton> { new("a", "a"), new("b", "b"), new("c", "c") });
            var rows = Layout(Key.CreateMarkupFromMenu(menu));
            Assert.AreEqual(2, rows.Length);
            CollectionAssert.AreEqual(new[] { "a", "b" }, rows[0]);
            CollectionAssert.AreEqual(new[] { "c" }, rows[1]);
        }

        [TestMethod]
        public void NullButtons_AreLeftOut_AndTheRestCloseUp()
        {
            // The report menu leaves the go-to-chat slot null in a group without a link.
            var menu = new Menu(2, new List<InlineButton> { new("a", "a"), null, new("b", "b"), new("c", "c"), null });
            var errors = new List<string>();
            var previous = ErrorReporter.Observer;
            ErrorReporter.Observer = errors.Add;
            try
            {
                var rows = Layout(Key.CreateMarkupFromMenu(menu));
                Assert.AreEqual(2, rows.Length);
                CollectionAssert.AreEqual(new[] { "a", "b" }, rows[0]);
                CollectionAssert.AreEqual(new[] { "c" }, rows[1]);
            }
            finally
            {
                ErrorReporter.Observer = previous;
            }
            Assert.AreEqual(0, errors.Count, string.Join("\n", errors));
        }

        [TestMethod]
        public void SeveralMenus_SkipNullsToo()
        {
            var rows = Layout(Key.CreateMarkupFromMenus(
                new Menu(1, new List<InlineButton> { null, new("a", "a") }),
                new Menu(2, new List<InlineButton> { new("b", "b"), null, new("c", "c") })));
            Assert.AreEqual(2, rows.Length);
            CollectionAssert.AreEqual(new[] { "a" }, rows[0]);
            CollectionAssert.AreEqual(new[] { "b", "c" }, rows[1]);
        }
    }
}
