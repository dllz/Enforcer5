using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Enforcer5.Helpers;

namespace Enforcer5.Tests
{
    /// <summary>
    /// Which language file a member's Telegram language code picks (2026-11-05): files without a code threw for every
    /// "zh" and "zh-hans" user, who got the fallback.
    /// </summary>
    [TestClass]
    public class LanguageCodeTests
    {
        private static readonly Language[] Files =
        {
            new() { Name = "English", IEFT = "en" },
            new() { Name = "Mandarin, no code" },
            new() { Name = "Cantonese", IEFT = "zh-yue-hk" },
            new() { Name = "Simplified", IEFT = "zh-hans" },
            new() { Name = "Portuguese", IEFT = "pt-br" },
            new() { Name = "Spanish, no code" }
        };

        [DataTestMethod]
        [DataRow("en", "English")]
        [DataRow("EN", "English")]
        [DataRow("zh-hans", "Simplified")]
        [DataRow("zh", "Simplified")] // the shorter of the two zh codes
        [DataRow("zh-hant", "Simplified")]
        [DataRow("pt", "Portuguese")]
        [DataRow("pt-pt", "Portuguese")]
        public void ACode_FindsItsFile_SkippingFilesWithoutOne(string code, string expected) =>
            Assert.AreEqual(expected, Methods.LanguageForCode(Files, code)?.Name);

        [DataTestMethod]
        [DataRow("es")]
        [DataRow("xx")]
        [DataRow("")]
        [DataRow(null)]
        public void NoFile_IsNull(string code) => Assert.IsNull(Methods.LanguageForCode(Files, code));

        [TestMethod]
        public void OnlyFilesWithoutACode_IsNull_NotAnException() =>
            Assert.IsNull(Methods.LanguageForCode(Files.Where(f => f.IEFT == null), "zh"));
    }
}
