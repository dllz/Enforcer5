using System.IO;
using System.Linq;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests
{
    /// <summary>Info lines reach app.log beside error.log (2026-11-05), at most a few a second.</summary>
    [TestClass]
    [DoNotParallelize] // one log file
    public class LogHelperTests
    {
        [TestMethod]
        public void InfoLines_AreWrittenToAppLog()
        {
            var path = Path.Combine(Bot.LogDirectory, "app.log");
            if (File.Exists(path)) File.Delete(path);
            var tag = System.Guid.NewGuid().ToString("N");
            // Within one second unless the machine stalls; then fewer are cut, and the first check still holds.
            for (var i = 0; i < LogHelper.InfoPerSecond + 30; i++) LogHelper.Info($"{tag} line {i}");
            LogHelper.Info($"{tag} after");

            var lines = File.ReadAllLines(path).Where(l => l.Contains(tag)).ToList();
            StringAssert.StartsWith(lines[0], "[INFO] ");
            StringAssert.EndsWith(lines[0], $"{tag} line 0");
            Assert.IsTrue(lines.Count >= LogHelper.InfoPerSecond, "a second's worth always gets through");
        }
    }
}
