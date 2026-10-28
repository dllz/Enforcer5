using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Enforcer5.Data;
using Enforcer5.Detection;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types;

namespace Enforcer5.Tests.Detection
{
    /// <summary>
    /// Detection step 13: the similarity checks against the must and must-not fixtures (Fixtures/similarity.json), the
    /// thresholds in one place, and the dev commands' text.
    /// </summary>
    [TestClass]
    public class SimilarityTuningTests
    {
        private sealed record Case(string Kind, string A, string B, string Expect, string Note, string Known);

        private static IReadOnlyList<Case> Cases()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Detection", "Fixtures", "similarity.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.GetProperty("cases").EnumerateArray().Select(c => new Case(
                c.GetProperty("kind").GetString(), c.GetProperty("a").GetString(), c.GetProperty("b").GetString(),
                c.GetProperty("expect").GetString(), c.GetProperty("note").GetString(),
                c.TryGetProperty("known", out var known) ? known.GetString() : null)).ToList();
        }

        [TestCleanup]
        public void Reset() => SimilarityTuning.ResetForTests();

        private static bool NameMatches(string a, string b) =>
            NameSigns.Impersonates(new User { Id = 1, FirstName = a }, new[] { (new User { Id = 2, FirstName = b }, "admin") }) != null;

        private static bool TextMatches(string a, string b) =>
            MessageSigns.Alike(MessageSigns.ContentOf(new Message { Text = a }, false), MessageSigns.ContentOf(new Message { Text = b }, false))
            >= SimilarityTuning.Text;

        [TestMethod]
        public void EveryFixture_IsJudgedAsItMustBe()
        {
            var cases = Cases();
            var wrong = new List<string>();
            foreach (var c in cases.Where(c => c.Kind is "name" or "text"))
            {
                var matched = c.Kind == "name" ? NameMatches(c.A, c.B) : TextMatches(c.A, c.B);
                var right = matched == (c.Expect == "match");
                // A known miss that is judged right now: its "known" note is out of date.
                if (right == (c.Known != null)) wrong.Add($"{c.Kind} must {(c.Expect == "match" ? "" : "not ")}match: {c.Note}" +
                                                          (c.Known != null ? " (marked known, but now right)" : ""));
            }
            Assert.AreEqual(0, wrong.Count, string.Join("\n", wrong));
            Assert.IsTrue(cases.Count(c => c.Kind == "name") >= 10 && cases.Count(c => c.Kind == "text") >= 6);
            Assert.AreEqual(5, cases.Count(c => c.Kind == "text-embedding"), "the 5-language scam, for the AI step");
        }

        [TestMethod]
        public void Jaccard_IsSharedOverAll()
        {
            var a = new HashSet<int> { 1, 2, 3, 4 };
            var b = new HashSet<int> { 3, 4, 5, 6 };
            Assert.AreEqual(2.0 / 6, SimilarityTuning.Jaccard(a, b), 1e-9);
            Assert.AreEqual(0, SimilarityTuning.Jaccard(a, null));
            Assert.AreEqual(1, SimilarityTuning.TextSimilarity("Join NOW!!", "join now 2"));
        }

        [TestMethod]
        public void NameSimilarity_IsOneMinusTheShareOfLettersApart()
        {
            Assert.AreEqual(1, SimilarityTuning.NameSimilarity("daniel", "daniel"));
            Assert.AreEqual(1 - 1.0 / 7, SimilarityTuning.NameSimilarity("daniell", "daniel"), 1e-9);
            Assert.AreEqual(1 - 1.0 / 6, SimilarityTuning.NameSimilarity("danile", "daniel"), 1e-9, "a swap counts once");
        }

        [TestMethod]
        public void TheThresholds_ChangeWhatMatches()
        {
            Assert.IsFalse(NameMatches("Danielxy", "Daniel"));
            SimilarityTuning.NameShortDistance = 2;
            Assert.IsTrue(NameMatches("Danielxy", "Daniel"));

            const string a = "Join the best crypto signals channel today, profit guaranteed for everyone";
            const string b = "Join the best crypto signals group today, profit guaranteed for everybody";
            Assert.IsFalse(TextMatches(a, b));
            SimilarityTuning.Text = 0.65;
            Assert.IsTrue(TextMatches(a, b));
        }

        [TestMethod]
        public void Closest_FindsANearName_AndNothingFarAway()
        {
            var admin = new User { Id = 2, FirstName = "Christopher" };
            var near = NameSigns.Closest(new User { Id = 1, FirstName = "Christophe Rr" }, new[] { (admin, "admin") });
            Assert.IsNotNull(near);
            Assert.IsTrue(near.Value.Similarity >= SimilarityTuning.NameNear);
            Assert.IsNull(NameSigns.Closest(new User { Id = 1, FirstName = "Zoe" }, new[] { (admin, "admin") }));
        }

        [TestMethod]
        public void Remember_GivesTheSimilarity_AndTheClosestNearMiss()
        {
            MessageSigns.ResetForTests();
            const long chat = -100777;
            MessageSigns.Content C(string text) => MessageSigns.ContentOf(new Message { Text = text }, false);
            MessageSigns.Remember(chat, 1, 1, C("Join the best crypto signals channel today, profit guaranteed for everyone"));
            var (others, _, near) = MessageSigns.Remember(chat, 2, 2, C("Join the best crypto signals group today, profit guaranteed for everybody"));
            Assert.AreEqual(0, others.Count);
            Assert.IsTrue(near >= SimilarityTuning.TextNear && near < SimilarityTuning.Text, $"near {near}");
            var (same, similarity, _) = MessageSigns.Remember(chat, 3, 3, C("Join the best crypto signals channel today, profit guaranteed for everyone!!"));
            CollectionAssert.AreEqual(new[] { 1L }, same.ToArray());
            Assert.AreEqual(1, similarity);
            MessageSigns.ResetForTests();
        }

        [TestMethod]
        public void SimTest_SaysHowAlike()
        {
            var text = Commands.Compare("Dаniel | Support", "Daniel");
            StringAssert.Contains(text, "copies it (keyword)");
            StringAssert.Contains(text, "Staff word: first");
            StringAssert.Contains(Commands.Compare("Free crypto now", "Free crypto now!!"), "Text: 1.00 → same");
        }

        [TestMethod]
        public void Review_ShowsHowSureEachSignIs()
        {
            var text = Commands.Review(new[] { new SignStat("link_outside", 40, 30, 4, 6, 2, 1, 1, 0) }, (3, 20),
                new[] { new SimilarityBucket("same_as_newcomers", "near", 0.6, 12, 5, 1) }, 30);
            StringAssert.Contains(text, "<code>link_outside</code>: 40/30/4 · 6, 2, 1, 1 · 80%");
            StringAssert.Contains(text, "0.6+ near: 12 (5/1)");
            StringAssert.Contains(text, "3 of 20");
            StringAssert.Contains(text, "SimilarityText=0.7");
        }
    }
}
