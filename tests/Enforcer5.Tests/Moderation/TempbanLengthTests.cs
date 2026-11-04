using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Moderation
{
    /// <summary>/tempban's units: "2 h" is two hours, not two minutes (issue #31).</summary>
    [TestClass]
    public class TempbanLengthTests
    {
        [DataTestMethod]
        [DataRow("h", 120.0)] [DataRow("hours", 120.0)] [DataRow("HR", 120.0)]
        [DataRow("d", 2880.0)] [DataRow("day", 2880.0)]
        [DataRow("w", 20160.0)] [DataRow("weeks", 20160.0)]
        [DataRow("m", 2.0)] [DataRow("min", 2.0)] [DataRow("", 2.0)] [DataRow(null, 2.0)]
        [DataRow("spamming", 2.0)] // the start of a reason: minutes, as before
        public void Units(string units, double minutes) => Assert.AreEqual(minutes, Commands.TempbanMinutes(2, units));
    }
}
