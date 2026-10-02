using Enforcer5.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Mutes
{
    [TestClass]
    public class RestrictFailureTests
    {
        // The enum is internal, so the rows name it by text.
        [DataTestMethod]
        [DataRow("Bad Request: not enough rights to restrict/unrestrict chat member", nameof(RestrictFailure.BotNotAdmin))]
        [DataRow("Bad Request: Not enough rights to mute chat member", nameof(RestrictFailure.BotNotAdmin))]
        [DataRow("Bad Request: CHAT_ADMIN_REQUIRED", nameof(RestrictFailure.BotNotAdmin))]
        [DataRow("Bad Request: user is an administrator of the chat", nameof(RestrictFailure.TargetIsAdmin))]
        [DataRow("Bad Request: can't remove chat owner", nameof(RestrictFailure.TargetIsAdmin))]
        [DataRow("Bad Request: method is available only for supergroups", nameof(RestrictFailure.Other))]
        [DataRow("Bad Request: user not found", nameof(RestrictFailure.Other))]
        [DataRow(null, nameof(RestrictFailure.Other))]
        public void TelegramErrors_AreSorted(string description, string expected)
        {
            Assert.AreEqual(expected, RestrictFailures.Classify(description).ToString());
        }
    }
}
