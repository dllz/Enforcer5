using System;
using System.IO;
using System.Linq;
using Enforcer5.Recovery;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Recovery
{
    /// <summary>
    /// The file a dev imports with /recovery: only what an offer may set gets through, for its own group and member.
    /// </summary>
    [TestClass]
    public class RecoveryFileTests
    {
        private const long Chat = -1001234567890;

        private static string File(string items, long chat = Chat) => $@"{{
  ""batch"": ""test-1"", ""from"": ""2026-08-22T04:12:00Z"", ""to"": ""2026-10-26T09:26:00Z"",
  ""groups"": [ {{ ""chat"": {chat}, ""items"": [ {items} ] }} ] }}";

        private const string Warns = @"{ ""kind"": ""warns"", ""member"": 42,
  ""changes"": [ { ""type"": ""hash"", ""key"": ""chat:-1001234567890:warns"", ""field"": ""42"", ""before"": ""1"", ""after"": ""3"" },
                 { ""type"": ""hash"", ""key"": ""chat:-1001234567890:totalWarns"", ""field"": ""42"", ""before"": null, ""after"": ""3"" } ],
  ""evidence"": [ { ""at"": ""2026-09-10T14:33:00Z"", ""log"": -1009, ""post"": 77, ""what"": ""/warn 42"" } ] }";

        [TestMethod]
        public void AFile_IsReadWithEveryKind()
        {
            var parsed = RecoveryFile.Parse(File(Warns + @",
{ ""kind"": ""reportblock"", ""member"": 43, ""changes"": [ { ""type"": ""set"", ""key"": ""chat:-1001234567890:reportblocked"", ""field"": ""43"", ""before"": ""false"", ""after"": ""true"" } ] },
{ ""kind"": ""inline"", ""changes"": [ { ""type"": ""inline"", ""key"": ""chat:-1001234567890:blockedinline"", ""field"": ""/[Ww]hisper/"", ""before"": ""false"", ""after"": ""true"" } ] },
{ ""kind"": ""stickerset"", ""changes"": [ { ""type"": ""stickerset"", ""key"": ""chat:-1001234567890:blockedstickersets"", ""field"": ""pack_by_bot"", ""before"": ""false"", ""after"": ""true"" } ] },
{ ""kind"": ""extra"", ""changes"": [ { ""type"": ""hash"", ""key"": ""chat:-1001234567890:extra"", ""field"": ""#rules"", ""before"": ""old"", ""after"": null } ] },
{ ""kind"": ""warnreasons"", ""changes"": [ { ""type"": ""str"", ""key"": ""chat:-1001234567890:warnreasons"", ""field"": null, ""before"": ""[]"", ""after"": ""[\""Mistake\""]"" } ] }"));

            Assert.AreEqual("test-1", parsed.Batch);
            Assert.AreEqual(new DateTimeOffset(2026, 8, 22, 4, 12, 0, TimeSpan.Zero), parsed.From);
            var offer = parsed.Offers.Single();
            Assert.AreEqual(Chat, offer.ChatId);
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6 }, offer.Items.Select(i => i.Position).ToArray());
            var warns = offer.Items[0];
            Assert.AreEqual(42L, warns.MemberId);
            Assert.AreEqual("3", warns.Changes[0].After);
            Assert.IsNull(warns.Changes[1].Before);
            Assert.AreEqual(77L, warns.Evidence.Single().Post);
            Assert.IsTrue(offer.Items.All(i => i.Selected));
        }

        [DataTestMethod]
        [DataRow("chat:-1009999:warns", "42", "1", "3", DisplayName = "another group's key")]
        [DataRow("chat:-1001234567890:settings", "42", "1", "3", DisplayName = "a key warns may not set")]
        [DataRow("chat:-1001234567890:warns", "41", "1", "3", DisplayName = "another member")]
        [DataRow("chat:-1001234567890:warns", "42", "1", "lots", DisplayName = "a count that is not a number")]
        [DataRow("chat:-1001234567890:warns", "42", "1", "-2", DisplayName = "a negative count")]
        [DataRow("chat:-1001234567890:warns", "42", "3", "3", DisplayName = "no change")]
        public void AChangeAWarnsItemMayNotMake_IsRefused(string key, string field, string before, string after)
        {
            var item = $@"{{ ""kind"": ""warns"", ""member"": 42, ""changes"": [ {{ ""type"": ""hash"", ""key"": ""{key}"", ""field"": ""{field}"", ""before"": ""{before}"", ""after"": ""{after}"" }} ] }}";
            Assert.ThrowsException<FormatException>(() => RecoveryFile.Parse(File(item)));
        }

        [TestMethod]
        public void TheWholeFile_IsRefusedForOneBadItem()
        {
            var bad = @"{ ""kind"": ""extra"", ""changes"": [ { ""type"": ""hash"", ""key"": ""chat:-1001234567890:extra"", ""field"": ""rules"", ""before"": null, ""after"": ""x"" } ] }";
            var e = Assert.ThrowsException<FormatException>(() => RecoveryFile.Parse(File(Warns + "," + bad)));
            StringAssert.Contains(e.Message, "item 2");
        }

        [DataTestMethod]
        [DataRow(@"{ ""kind"": ""settings"", ""changes"": [] }", DisplayName = "unknown kind")]
        [DataRow(@"{ ""kind"": ""warns"", ""changes"": [ { ""type"": ""hash"", ""key"": ""chat:-1001234567890:warns"", ""field"": ""42"", ""before"": null, ""after"": ""1"" } ] }", DisplayName = "warns without a member")]
        [DataRow(@"{ ""kind"": ""inline"", ""changes"": [ { ""type"": ""inline"", ""key"": ""chat:-1001234567890:blockedinline"", ""field"": ""not a bot!"", ""before"": ""false"", ""after"": ""true"" } ] }", DisplayName = "not an inline bot")]
        [DataRow(@"{ ""kind"": ""stickerset"", ""changes"": [ { ""type"": ""stickerset"", ""key"": ""chat:-1001234567890:blockedstickersets"", ""field"": ""p"", ""before"": ""no"", ""after"": ""yes"" } ] }", DisplayName = "a flag that is not true or false")]
        [DataRow(@"{ ""kind"": ""warnreasons"", ""changes"": [ { ""type"": ""hash"", ""key"": ""chat:-1001234567890:warnreasons"", ""field"": ""x"", ""before"": null, ""after"": ""x"" } ] }", DisplayName = "the wrong type for its kind")]
        public void AnItemThatIsNotAnOffer_IsRefused(string item)
        {
            Assert.ThrowsException<FormatException>(() => RecoveryFile.Parse(File(item)));
        }

        [DataTestMethod]
        [DataRow(@"{ ""batch"": ""a b"", ""from"": ""2026-08-22T00:00:00Z"", ""to"": ""2026-10-26T00:00:00Z"", ""groups"": [] }", DisplayName = "batch name")]
        [DataRow(@"{ ""batch"": ""x"", ""from"": ""2026-10-26T00:00:00Z"", ""to"": ""2026-08-22T00:00:00Z"", ""groups"": [] }", DisplayName = "period backwards")]
        [DataRow(@"{ ""batch"": ""x"", ""from"": ""2026-08-22T00:00:00Z"", ""to"": ""2026-10-26T00:00:00Z"", ""groups"": [] }", DisplayName = "no groups")]
        [DataRow("not json", DisplayName = "not json")]
        public void AFileThatIsNotARecoveryFile_IsRefused(string json)
        {
            Assert.ThrowsException<FormatException>(() => RecoveryFile.Parse(json));
        }

        [TestMethod]
        public void TheSameGroupTwice_IsRefused()
        {
            var json = File(Warns).Replace(@"""groups"": [", @"""groups"": [ { ""chat"": " + Chat + @", ""items"": [ " + Warns + " ] },");
            Assert.ThrowsException<FormatException>(() => RecoveryFile.Parse(json));
        }

        /// <summary>
        /// A real recovery file, checked before it is sent to the bot: <c>ENFORCER_RECOVERY_FILE=path dotnet test
        /// --filter RecoveryFileTests</c>. Skipped without it (real files hold member ids and never go in the repo).
        /// </summary>
        [TestMethod]
        public void TheFileInTheEnvironment_IsAccepted()
        {
            var path = Environment.GetEnvironmentVariable("ENFORCER_RECOVERY_FILE");
            if (string.IsNullOrEmpty(path)) Assert.Inconclusive("ENFORCER_RECOVERY_FILE is not set");
            var parsed = RecoveryFile.Parse(System.IO.File.ReadAllText(path));
            Console.WriteLine($"{parsed.Batch}: {parsed.Offers.Count} groups, {parsed.Offers.Sum(o => o.Items.Count)} items");
        }
    }
}
