using System.Linq;
using System.Xml.Linq;
using Enforcer5.Detection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests.Detection
{
    /// <summary>Detection step 7: the pure parts of reports, and the strings they build their text from.</summary>
    [TestClass]
    public class DetectionReportTests
    {
        private static XDocument _english;

        [ClassInitialize]
        public static void Load(TestContext _) => _english = XDocument.Load(TestEnvironment.EnglishXmlPath);

        private static bool Has(string key) => _english.Descendants("string").Any(s => (string)s.Attribute("key") == key);

        [TestMethod]
        public void EverySignThatCanBeReported_HasAName()
        {
            var missing = SignCatalog.All.Where(s => !s.Ported && !s.Name.StartsWith("test_"))
                .Select(s => $"sign_{s.Name}").Where(k => !Has(k)).ToList();
            Assert.AreEqual(0, missing.Count, string.Join(", ", missing));
        }

        [TestMethod]
        public void EveryAction_AndEveryDecision_HasItsString()
        {
            var keys = DetectionPolicy.Actions.Select(a => $"filterAction_{a}")
                .Concat(new[] { "ban", "mute", "fine", "trial_live", "trial_extend", "trial_delete" }.Select(d => $"reportDecided_{d}"))
                .Concat(EngineSettings.ReportChoices.Select(c => $"reportsTo_{c}"));
            var missing = keys.Where(k => !Has(k)).ToList();
            Assert.AreEqual(0, missing.Count, string.Join(", ", missing));
        }

        [TestMethod]
        public void ReportDestinations_FallBackToTheAdmins_WithoutALinkedChat()
        {
            var engine = EngineSettings.Default;
            Assert.IsTrue(engine.ReportsToAdmins);
            Assert.IsNull(engine.ReportChat);

            var chatOnly = engine with { ReportTo = EngineSettings.ToAdminChat };
            Assert.IsTrue(chatOnly.ReportsToAdmins, "chosen, but no chat linked");
            Assert.IsNull(chatOnly.ReportChat);

            var linked = chatOnly with { AdminChatId = -100123 };
            Assert.IsFalse(linked.ReportsToAdmins);
            Assert.AreEqual(-100123, linked.ReportChat);

            var both = linked with { ReportTo = EngineSettings.ToBoth };
            Assert.IsTrue(both.ReportsToAdmins);
            Assert.AreEqual(-100123, both.ReportChat);

            Assert.IsNull((linked with { ReportTo = EngineSettings.ToAdmins }).ReportChat, "linked but not chosen");
        }

        [TestMethod]
        public void ThePolicyCache_KeepsWhereReportsGo()
        {
            var policy = DetectionPolicy.Empty with
            {
                Engine = EngineSettings.Default with { ReportTo = EngineSettings.ToBoth, AdminChatId = -100777 }
            };
            var back = DetectionPolicyJson.Deserialize(DetectionPolicyJson.Serialize(policy));
            Assert.AreEqual((EngineSettings.ToBoth, (long?)-100777), (back.Engine.ReportTo, back.Engine.AdminChatId));
            Assert.IsTrue(back.SameSettingsAs(policy));
        }

        [TestMethod]
        public void ButtonsFromReportsAndRaids_AreLabelledWithTheirOwnSource()
        {
            Assert.AreEqual("report", Decisions.ButtonSource($"{DetectionReports.Trigger}:-100:1:2:ban"));
            Assert.AreEqual("raid", Decisions.ButtonSource("raid:-100:ban:abc"));
            Assert.AreEqual("raid", Decisions.ButtonSource("raidlink:-100:ignore:1:2"));
            Assert.IsNull(Decisions.ButtonSource("banflag:-100:1:2"));
            Assert.IsNull(Decisions.ButtonSource(null));
        }
    }
}
