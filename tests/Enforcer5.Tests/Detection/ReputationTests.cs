using System;
using Enforcer5.Data;
using Enforcer5.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;

namespace Enforcer5.Tests.Detection
{
    /// <summary>Detection step 14: websites by their host, the /channelrep text, and what a member's name says.</summary>
    [TestClass]
    public class ReputationTests
    {
        [DataTestMethod]
        [DataRow("https://www.Shop.Example/deal?x=1", "shop.example")]
        [DataRow("http://user@pump.example:8080/a", "pump.example")]
        [DataRow("cheap.deals.example.", "cheap.deals.example")]
        [DataRow("https://t.me/somechannel", null)]
        [DataRow("telegram.org/faq", null)]
        [DataRow("localhost", null)]
        [DataRow("not a url", null)]
        public void DomainOf(string url, string expected) => Assert.AreEqual(expected, Reputation.DomainOf(url), url);

        [TestMethod]
        public void ReputationText_SaysWhetherItIsBad()
        {
            var at = new DateTimeOffset(2026, 10, 28, 3, 0, 0, TimeSpan.Zero);
            StringAssert.Contains(Commands.ReputationText("pump.example", new ReputationRow("domain", "pump.example", 3, 1, 2, 4, at, at)),
                "Reputation: bad");
            StringAssert.Contains(Commands.ReputationText("x.example", new ReputationRow("domain", "x.example", 3, 2, 2, 4, at, at)),
                "Reputation: not bad", "too many found fine");
            StringAssert.Contains(Commands.ReputationText("y.example", null), "no new member has pointed at it");
        }

        [TestMethod]
        public void Describe_KeepsFeatures_NotTheName()
        {
            var delta = new SignalDelta();
            MemberData.Describe(delta, new User { Id = 7_123_456_789, FirstName = "Аdmin 🔥", LastName = "24", LanguageCode = "RU", IsPremium = true });
            Assert.AreEqual(71, delta.AccountIdRange);
            Assert.AreEqual(false, delta.HasUsername);
            Assert.AreEqual("ru", delta.LanguageCode);
            Assert.AreEqual(true, delta.NameEmoji);
            Assert.AreEqual(true, delta.NameDigits);
            Assert.AreEqual(true, delta.NameStaffWord);
            Assert.AreEqual(5, delta.NameLetters);
        }
    }
}
