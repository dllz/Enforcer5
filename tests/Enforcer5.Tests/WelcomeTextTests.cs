using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;

namespace Enforcer5.Tests
{
    [TestClass]
    public class WelcomeTextTests
    {
        private static readonly User Joiner = new User { Id = 42, FirstName = "A<B & C", Username = "abc" };
        private const string Linked = "<a href=\"tg://user?id=42\">A&lt;B &amp; C</a>";

        [TestMethod]
        public void Name_IsALinkToTheProfile()
        {
            Assert.AreEqual($"Hi {Linked} in T&amp;T", WelcomeText.Html("Hi $name in $title", Joiner, "T&T"));
        }

        [TestMethod]
        public void NameInsideTheAdminsOwnLink_IsPlain()
        {
            Assert.AreEqual("<a href=\"tg://user?id=42\">A&lt;B &amp; C</a> and " + Linked,
                WelcomeText.Html("<a href=\"tg://user?id=$id\">$name</a> and $name", Joiner, "T"));
        }

        [TestMethod]
        public void NameInsideFormattingInsideALink_IsPlain()
        {
            Assert.AreEqual("<A HREF=\"https://x\"><b>A&lt;B &amp; C</b></A>",
                WelcomeText.Html("<A HREF=\"https://x\"><b>$name</b></A>", Joiner, "T"));
        }

        [TestMethod]
        public void NameInsideBold_IsLinked()
        {
            Assert.AreEqual($"<b>{Linked}</b>", WelcomeText.Html("<b>$name</b>", Joiner, "T"));
        }

        [TestMethod]
        public void OtherPlaceholders_AreFilled()
        {
            Assert.AreEqual("@abc 42", WelcomeText.Html("$username $id", Joiner, "T"));
            Assert.AreEqual("(no username)", WelcomeText.Html("$username", new User { Id = 1, FirstName = "x" }, "T"));
        }

        [TestMethod]
        public void Plain_IsTheTextAsWritten_WithThePlainName()
        {
            Assert.AreEqual("We <3 A<B & C in T&T", WelcomeText.Plain("We <3 $name in $title", Joiner, "T&T"));
        }

        [TestMethod]
        public void Unformat_DropsTheMarkup()
        {
            Assert.AreEqual("Welcome to G, A<B & C!\nRules: No <spam>",
                WelcomeText.Unformat($"Welcome to <b>G</b>, {Linked}!\n<b>Rules</b>: No <spam>"));
        }
    }
}
