using Enforcer5.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Detection
{
    /// <summary>Detection step 7c: an admin's reason that names spam or abuse, in the bot's languages.</summary>
    [TestClass]
    public class AbuseWordsTests
    {
        [DataTestMethod]
        [DataRow("spam")]
        [DataRow("Spamming links")]
        [DataRow("ads in pm")]
        [DataRow("crypto scam!")]
        [DataRow("Werbung")]
        [DataRow("pubblicità")]
        [DataRow("estafa")]
        [DataRow("dolandırıcı")]
        [DataRow("реклама канала")]
        [DataRow("рейд")]
        [DataRow("发广告")]
        [DataRow("تبلیغات")]
        [DataRow("fake admin")]
        public void NamesAbuse(string reason) => Assert.IsTrue(AbuseWords.Mentions(reason), reason);

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("leaked roles")]
        [DataRow("game rule: no revealing")]
        [DataRow("off topic")]
        [DataRow("added a friend")]
        [DataRow("bad advice")]
        [DataRow("rude")]
        public void LeavesGroupRulesAlone(string reason) => Assert.IsFalse(AbuseWords.Mentions(reason), reason ?? "null");
    }
}
