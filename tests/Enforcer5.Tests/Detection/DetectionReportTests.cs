using System;
using System.Linq;
using Enforcer5.Data;
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
        public void WhatASignPointedAt_IsNamed_FiveAtMost_LongOnesCut()
        {
            Assert.IsNull(DetectionReports.Named(new string[0]));
            Assert.AreEqual("@a, @b", DetectionReports.Named(new[] { "@a", "@b" }));
            Assert.AreEqual("1, 2, 3, 4, 5 +2", DetectionReports.Named(new[] { "1", "2", "3", "4", "5", "6", "7" }));
            Assert.AreEqual(60, DetectionReports.Named(new[] { new string('x', 200) }).Length);
        }

        [TestMethod]
        public void ShowMessages_OneEntryPerMessage_WithEverySign_OldestFirst_WithoutTextLeftOut()
        {
            var at = new DateTimeOffset(2026, 11, 6, 6, 0, 0, TimeSpan.Zero);
            var merged = DetectionReports.Merge(new[]
            {
                new FlaggedMessage("mention_outside", at.AddMinutes(1), 7, "join @x", "@x"),
                new FlaggedMessage("link_outside", at.AddMinutes(1), 7, "join @x", null),
                new FlaggedMessage("same_as_newcomers", at, 5, "earlier", null),
                new FlaggedMessage("same_as_newcomers", at.AddMinutes(2), null, "   ", null),
                new FlaggedMessage("mention_outside", at.AddMinutes(1), 7, "join @x", "@x")
            });
            Assert.AreEqual(2, merged.Count);
            Assert.AreEqual(5, merged[0].MessageId);
            Assert.AreEqual("mention_outside,link_outside", merged[1].Sign);
            Assert.AreEqual("@x", merged[1].Detail);
        }

        [TestMethod]
        public void ShowMessages_IsSplitIntoPartsThatFitAMessage_AndEscaped()
        {
            var at = new DateTimeOffset(2026, 11, 6, 6, 0, 0, TimeSpan.Zero);
            var flagged = Enumerable.Range(1, 5)
                .Select(i => new FlaggedMessage("link_outside", at.AddMinutes(i), i, new string('a', 1500) + "<b>", null)).ToList();
            var parts = DetectionReports.MessagesText(_english, "📝 heading", flagged);
            Assert.IsTrue(parts.Count > 1);
            Assert.IsTrue(parts.All(p => p.Length <= DetectionReports.MessagesPartLength + 300), string.Join(",", parts.Select(p => p.Length)));
            StringAssert.StartsWith(parts[0], "📝 heading");
            Assert.AreEqual(5, parts.Sum(p => p.Split("<blockquote expandable>").Length - 1));
            Assert.IsFalse(parts.Any(p => p.Contains("a<b>")), "the text is escaped");

            var huge = DetectionReports.MessagesText(_english, "h", new[] { new FlaggedMessage("link_outside", at, 1, new string('z', 4096), null) });
            Assert.AreEqual(1, huge.Count);
            Assert.IsTrue(huge[0].Length <= DetectionReports.MessagesPartLength + 64);
        }

        [TestMethod]
        public void TheStringsOfShowMessages_AreThere()
        {
            foreach (var key in new[] { "reportShowMessages", "reportMessagesTitle", "reportMessagesNone", "reportUnknownNames" })
                Assert.IsTrue(Has(key), key);
        }

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
                .Concat(new[] { "ban", "mute", "warn", "prewarn", "fine", "handled", "trial_live", "trial_extend", "trial_delete" }.Select(d => $"reportDecided_{d}"))
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
