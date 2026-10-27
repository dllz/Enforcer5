using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using StackExchange.Redis;

namespace Enforcer5.Tests.Moderation
{
    [TestClass]
    public class MaxWarnsTests
    {
        [TestMethod]
        public void AStoredPositiveMaximum_IsUsed() => Assert.AreEqual(3, Methods.MaxWarns((RedisValue)"3"));

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("0")]
        [DataRow("-1")]
        [DataRow("many")]
        public void NothingUsable_IsTheDefaultOfFive(string stored) =>
            Assert.AreEqual(5, Methods.MaxWarns(stored == null ? RedisValue.Null : (RedisValue)stored));
    }
}
