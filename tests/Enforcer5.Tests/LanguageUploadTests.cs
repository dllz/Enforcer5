using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Enforcer5.Handlers;
using Enforcer5.LanguageUpload;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Telegram.Bot.Types.ReplyMarkups;
using File = System.IO.File;

namespace Enforcer5.Tests
{
    /// <summary>Language uploads: unpacking, checking, grouping warnings, choosing files, the menu and publishing.</summary>
    [TestClass]
    public class LanguageUploadTests
    {
        private string _root;
        private string _live;
        private XDocument _master;

        private const string EnglishStrings =
            @"<string key=""Plain""><value>Hello</value></string>
              <string key=""WithName""><value>Hello {0}</value></string>
              <string key=""Other""><value>Bye</value></string>
              <string key=""Gif"" isgif=""true""><value>gif</value></string>
              <string key=""Old"" deprecated=""true""><value>old</value></string>";

        [TestInitialize]
        public void Init()
        {
            _root = Path.Combine(Path.GetTempPath(), "enf-langupload-" + Guid.NewGuid().ToString("N"));
            _live = Path.Combine(_root, "Languages");
            Directory.CreateDirectory(_live);
            File.WriteAllText(Path.Combine(_live, "English.xml"), Lang("English", "English", EnglishStrings));
            _master = XDocument.Load(Path.Combine(_live, "English.xml"));
        }

        [TestCleanup]
        public void Cleanup()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private static string Lang(string name, string @base, string strings) =>
            $@"<strings><language name=""{name}"" base=""{@base}""/>{strings}</strings>";

        /// <summary>A complete translation; <paramref name="withName"/> and <paramref name="extra"/> make problems.</summary>
        internal static string German(string name = "Deutsch", string @base = "German", string withName = "Hallo {0}", string extra = "") =>
            Lang(name, @base,
                $@"<string key=""Plain""><value>Hallo</value></string>
                   <string key=""WithName""><value>{withName}</value></string>
                   <string key=""Other""><value>Tschüss</value></string>
                   <string key=""Gif""><value>gif</value></string>{extra}");

        private const string OtherTwice = @"<string key=""Other""><value>Ciao</value></string>";

        private static IntakeEntry Entry(string fileName, string content) =>
            new IntakeEntry { FileName = fileName, Content = Encoding.UTF8.GetBytes(content) };

        internal static MemoryStream Zip(params (string Name, string Content)[] files)
        {
            var ms = new MemoryStream();
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
                foreach (var (name, content) in files)
                    using (var w = new StreamWriter(zip.CreateEntry(name).Open()))
                        w.Write(content);
            ms.Position = 0;
            return ms;
        }

        private LanguageBatch Build(params IntakeEntry[] entries) =>
            BatchBuilder.Build("0123456789ab", -100, DateTime.UtcNow, entries, Path.Combine(_root, "batch", "files"), _live, _master);

        // ---- checks ----

        [TestMethod]
        public void Checks_FindPlaceholdersDuplicatesGifsAndMissingStrings()
        {
            var doc = XDocument.Parse(Lang("X", "X",
                @"<string key=""WithName""><value>Hallo {1}</value></string>
                  <string key=""Other""><value>a</value></string><string key=""Other""><value>b</value></string>
                  <string key=""Gif""><value>" + new string('x', 201) + @"</value></string>
                  <string><value>no key</value></string>"));
            var errors = LanguageValidator.CheckStrings("X", doc, _master);

            Assert.IsTrue(errors.Any(e => e.Key == "WithName" && e.Message == "Missing {0}" && e.Level == ErrorLevel.Error));
            Assert.IsTrue(errors.Any(e => e.Key == "WithName" && e.Message == "Extra {1}" && e.Level == ErrorLevel.Error));
            Assert.AreEqual(1, errors.Count(e => e.Key == "Other" && e.Level == ErrorLevel.DuplicatedString));
            Assert.IsTrue(errors.Any(e => e.Key == "Gif" && e.Level == ErrorLevel.GifTooLong));
            Assert.IsTrue(errors.Any(e => e.Key == "Plain" && e.Level == ErrorLevel.MissingString));
            Assert.IsFalse(errors.Any(e => e.Key == "Old"), "a deprecated string is not missing");
        }

        // ---- intake ----

        [TestMethod]
        public void Intake_SingleXml_IsOneFile()
        {
            var entries = BatchIntake.Read("German.xml", new MemoryStream(Encoding.UTF8.GetBytes(German())));
            Assert.AreEqual(1, entries.Count);
            Assert.AreEqual("German.xml", entries[0].FileName);
            Assert.IsNull(entries[0].Rejected);
        }

        [TestMethod]
        public void Intake_Zip_TakesTopLevelXmlAndReportsTheRest()
        {
            var entries = BatchIntake.Read("langs.zip", Zip(
                ("German.xml", German()),
                ("sub/French.xml", German()),
                ("../Escape.xml", German()),
                ("notes.txt", "hi"),
                ("__MACOSX/._German.xml", "junk"),
                ("german.xml", German())));
            var byName = entries.ToDictionary(e => e.FileName);
            Assert.IsNull(byName["German.xml"].Rejected);
            Assert.IsNotNull(byName["sub/French.xml"].Rejected);
            Assert.IsNotNull(byName["../Escape.xml"].Rejected);
            Assert.IsNotNull(byName["notes.txt"].Rejected);
            Assert.IsNotNull(byName["german.xml"].Rejected, "the same name in another case would replace the same live file");
            Assert.IsFalse(byName.ContainsKey("__MACOSX/._German.xml"));
            Assert.IsTrue(entries.Where(e => e.Rejected != null).All(e => e.Content == null));
        }

        [TestMethod]
        public void Intake_TooManyFilesOrNotAZip_Throws()
        {
            var many = Enumerable.Range(0, BatchIntake.MaxFiles + 1).Select(i => ($"L{i}.xml", "<x/>")).ToArray();
            Assert.ThrowsExactly<InvalidDataException>(() => BatchIntake.Read("many.zip", Zip(many)));
            Assert.ThrowsExactly<InvalidDataException>(() => BatchIntake.Read("bad.zip", new MemoryStream(new byte[] { 1, 2, 3 })));
            Assert.ThrowsExactly<InvalidDataException>(() => BatchIntake.Read("file.txt", new MemoryStream()));
        }

        [TestMethod]
        public void Intake_OversizedFile_IsRejected()
        {
            var big = new string('x', BatchIntake.MaxFileBytes + 1);
            var entries = BatchIntake.Read("big.zip", Zip(("Big.xml", big)));
            StringAssert.Contains(entries.Single().Rejected, "larger");
        }

        [TestMethod]
        public void PlainXmlName_RefusesPathsAndHiddenFiles()
        {
            Assert.IsTrue(BatchIntake.IsPlainXmlName("Bahasa_Indonesia.xml"));
            Assert.IsFalse(BatchIntake.IsPlainXmlName("../x.xml"));
            Assert.IsFalse(BatchIntake.IsPlainXmlName("a\\x.xml"));
            Assert.IsFalse(BatchIntake.IsPlainXmlName(".x.xml"));
            Assert.IsFalse(BatchIntake.IsPlainXmlName("x.txt"));
        }

        // ---- building a batch ----

        [TestMethod]
        public void Build_SortsFilesIntoCleanWarningAndFatal()
        {
            File.WriteAllText(Path.Combine(_live, "French.xml"), Lang("Français", "French", ""));
            var batch = Build(
                Entry("German.xml", German()),
                Entry("Placeholder.xml", German("P", "P", withName: "Hallo")),
                Entry("Broken.xml", "<strings><language"),
                Entry("Clash.xml", Lang("Français", "Other", "")),
                Entry("NoBase.xml", "<strings><language name=\"NoBase\"/></strings>"),
                new IntakeEntry { FileName = "notes.txt", Rejected = "not an .xml file" });

            var f = batch.Files.ToDictionary(x => x.FileName);
            Assert.IsTrue(f["German.xml"].IsClean);
            Assert.IsTrue(f["German.xml"].IsNewLanguage);
            Assert.IsFalse(f["Placeholder.xml"].IsClean);
            Assert.IsFalse(f["Placeholder.xml"].IsFatal, "a placeholder problem can be accepted");
            Assert.IsTrue(f["Broken.xml"].IsFatal);
            StringAssert.Contains(IssueText.FatalReason(f["Broken.xml"]), "not valid XML");
            Assert.IsTrue(f["Clash.xml"].IsFatal, "same language name as a live file, another base");
            Assert.IsTrue(f["NoBase.xml"].IsFatal);
            Assert.IsTrue(f["notes.txt"].IsFatal);
        }

        [TestMethod]
        public void Build_ReplacingALiveFile_KnowsItsMissingStrings()
        {
            File.WriteAllText(Path.Combine(_live, "German.xml"), Lang("Deutsch", "German", @"<string key=""Plain""><value>Hallo</value></string>"));
            var file = Build(Entry("German.xml", German())).Files.Single();
            Assert.IsFalse(file.IsNewLanguage);
            Assert.AreEqual(3, file.LiveMissing);
            Assert.AreEqual(0, file.Missing);
            Assert.IsTrue(file.IsClean);
        }

        [TestMethod]
        public void Build_TwoFilesForOneLanguage_AreBothFatal()
        {
            var batch = Build(Entry("German.xml", German()), Entry("Deutsch.xml", German()));
            Assert.IsTrue(batch.Files.All(x => x.IsFatal));
        }

        // ---- warnings and selection ----

        /// <summary>A: placeholder in WithName. B: the same, and Other twice. C: clean. D: fatal (no base).</summary>
        private LanguageBatch Mixed() => Build(
            Entry("A.xml", German("A", "A", withName: "Hallo")),
            Entry("B.xml", German("B", "B", withName: "Hallo", extra: OtherTwice)),
            Entry("C.xml", German("C", "C")),
            Entry("D.xml", "<strings><language name=\"D\"/></strings>"));

        [TestMethod]
        public void IssueGroups_AreOnePerKindAndKey_MostWidespreadFirst()
        {
            var groups = Mixed().IssueGroups();
            Assert.AreEqual(2, groups.Count);
            Assert.AreEqual("WithName", groups[0].Key);
            CollectionAssert.AreEquivalent(new[] { "A.xml", "B.xml" }, groups[0].Files);
            Assert.AreEqual("Other", groups[1].Key);
            Assert.AreEqual("WithName: Missing {0}", IssueText.Label(groups[0]));
            Assert.AreEqual("Other twice", IssueText.Label(groups[1]));
        }

        [TestMethod]
        public void AcceptingAWarning_SelectsOnlyFilesWithNothingElseOutstanding()
        {
            var b = Mixed();
            CollectionAssert.AreEqual(new[] { "C.xml" }, b.Selected().Select(x => x.FileName).ToList());

            Assert.IsTrue(b.ToggleIssue(0));
            CollectionAssert.AreEqual(new[] { "A.xml", "C.xml" }, b.Selected().Select(x => x.FileName).ToList(),
                "B also has Other twice, which is not accepted");

            Assert.IsTrue(b.ToggleIssue(1));
            CollectionAssert.AreEqual(new[] { "A.xml", "B.xml", "C.xml" }, b.Selected().Select(x => x.FileName).ToList());

            Assert.IsTrue(b.ToggleIssue(0)); // un-accept
            CollectionAssert.AreEqual(new[] { "C.xml" }, b.Selected().Select(x => x.FileName).ToList());
            CollectionAssert.AreEqual(new[] { "C.xml" }, b.Clean().Select(x => x.FileName).ToList());
            Assert.IsFalse(b.ToggleIssue(5));
        }

        [TestMethod]
        public void PickingByHand_OverridesWarnings_ButNeverAFatalFile()
        {
            var b = Mixed();
            Assert.IsTrue(b.ToggleFile(1)); // B in
            Assert.IsTrue(b.ToggleFile(2)); // C out
            CollectionAssert.AreEqual(new[] { "B.xml" }, b.Selected().Select(x => x.FileName).ToList());
            Assert.IsFalse(b.ToggleFile(3), "D is fatal");
            Assert.IsFalse(b.ToggleFile(9));
            Assert.IsFalse(b.IsSelected(b.Files[3]));
        }

        // ---- menu ----

        private static IEnumerable<InlineKeyboardButton> Buttons(Screen s) =>
            s.Markup?.InlineKeyboard.SelectMany(r => r).Where(b => b.CallbackData != null) ?? Enumerable.Empty<InlineKeyboardButton>();

        [TestMethod]
        public void EveryButton_FitsTelegramsCallbackLimit_AndParsesBack()
        {
            var entries = Enumerable.Range(0, 30)
                .Select(i => Entry($"A very long language file name number {i:00} (with a theme).xml",
                    German($"L{i}", $"B{i}", withName: "Hallo", extra: i % 2 == 0 ? OtherTwice : "")))
                .ToArray();
            var b = Build(entries);
            b.ToggleIssue(0);
            var screens = new[]
            {
                BatchMenu.Public(b), BatchMenu.Summary(b), BatchMenu.Issues(b, 0), BatchMenu.Files(b, 0), BatchMenu.Files(b, 3),
                BatchMenu.Confirm(b, BatchAction.UploadSelected), BatchMenu.Confirm(b, BatchAction.Discard)
            };
            foreach (var s in screens)
            {
                Assert.IsTrue(s.Text.Length <= 4096, s.Text);
                foreach (var button in Buttons(s))
                {
                    Assert.IsTrue(Encoding.UTF8.GetByteCount(button.CallbackData) <= 64, button.CallbackData);
                    // split as UpdateHandler splits callback data
                    Assert.IsTrue(BatchMenu.TryParse(button.CallbackData.Split(':'), out var id, out _, out _), button.CallbackData);
                    Assert.AreEqual(b.Id, id);
                }
            }
        }

        [TestMethod]
        public void Files_ArePaged()
        {
            var b = Build(Enumerable.Range(0, 20).Select(i => Entry($"L{i}.xml", German($"L{i}", $"B{i}"))).ToArray());
            var first = BatchMenu.Files(b, 0);
            Assert.AreEqual(BatchMenu.PageSize, Buttons(first).Count(x => x.CallbackData.Contains(":tf:")));
            Assert.IsTrue(Buttons(first).Any(x => x.CallbackData == BatchMenu.Data(b.Id, BatchMenu.Ops.Files, 1)));
            var last = BatchMenu.Files(b, 99);
            Assert.AreEqual(20 - 2 * BatchMenu.PageSize, Buttons(last).Count(x => x.CallbackData.Contains(":tf:")));
            Assert.AreEqual(2, BatchMenu.PageOf(BatchMenu.PageSize * 2 + 1));
        }

        [TestMethod]
        public void TryParse_RefusesForgedData()
        {
            Assert.IsFalse(BatchMenu.TryParse("lb:../../etc:sum:0".Split(':'), out _, out _, out _));
            Assert.IsFalse(BatchMenu.TryParse("lb:0123456789ab:sum".Split(':'), out _, out _, out _));
            Assert.IsFalse(BatchMenu.TryParse("upload:-100:German".Split(':'), out _, out _, out _));
            Assert.IsTrue(BatchMenu.TryParse("lb:0123456789ab:ti:3".Split(':'), out _, out var op, out var arg));
            Assert.AreEqual(("ti", 3), (op, arg));
        }

        [TestMethod]
        public void Summary_OffersCleanAndSelectedUploads()
        {
            var b = Mixed();
            var data = Buttons(BatchMenu.Summary(b)).Select(x => x.CallbackData).ToList();
            CollectionAssert.Contains(data, BatchMenu.Data(b.Id, BatchMenu.Ops.Ask, (int)BatchAction.UploadClean));
            CollectionAssert.DoesNotContain(data, BatchMenu.Data(b.Id, BatchMenu.Ops.Ask, (int)BatchAction.UploadSelected),
                "selected is the same as clean until something is accepted");
            b.ToggleIssue(0);
            data = Buttons(BatchMenu.Summary(b)).Select(x => x.CallbackData).ToList();
            CollectionAssert.Contains(data, BatchMenu.Data(b.Id, BatchMenu.Ops.Ask, (int)BatchAction.UploadSelected));
            StringAssert.Contains(BatchMenu.Summary(b).Text, "D.xml");
        }

        [TestMethod]
        public void Result_SaysWhatWentLiveAndWhatDidNot()
        {
            var b = Mixed();
            var text = BatchMenu.Result(b, new List<PublishOutcome>
            {
                new PublishOutcome { FileName = "A.xml", Published = true },
                new PublishOutcome { FileName = "B.xml", Reason = "not selected" },
                new PublishOutcome { FileName = "C.xml", Reason = "now clashes with X.xml" },
                new PublishOutcome { FileName = "D.xml", Reason = "cannot be uploaded" },
            }, "Ann <admin>");
            StringAssert.Contains(text, "Uploaded 1 of 4");
            StringAssert.Contains(text, "Ann &lt;admin&gt;");
            StringAssert.Contains(text, "❌ C.xml (now clashes with X.xml)");
        }

        [TestMethod]
        public void Report_ListsEveryProblem()
        {
            var report = BatchReport.Render(Mixed());
            StringAssert.Contains(report, "B.xml");
            StringAssert.Contains(report, "Placeholders:\n  WithName: Missing {0}");
            StringAssert.Contains(report, "Twice (the bot may show either copy):\n  Other");
            StringAssert.Contains(report, "Base is missing");
        }

        // ---- store ----

        [TestMethod]
        public void Store_KeepsDecisions_AndExpiresOldBatches()
        {
            var store = new BatchStore(Path.Combine(_root, "batches"));
            var b = Mixed();
            b.ToggleIssue(0);
            b.ToggleFile(2);
            store.Save(b);

            var loaded = store.Load(b.Id);
            CollectionAssert.AreEqual(b.Selected().Select(x => x.FileName).ToList(), loaded.Selected().Select(x => x.FileName).ToList());
            Assert.AreEqual(b.Files[3].Errors.Count, loaded.Files[3].Errors.Count);
            Assert.AreEqual(ErrorLevel.FatalError, loaded.Files[3].Errors[0].Level);
            Assert.IsTrue(loaded.IsOpen);

            Assert.IsNull(store.Load("../batches"));
            Assert.IsNull(store.Change("ffffffffffff", x => x));

            store.RemoveExpired(DateTime.UtcNow);
            Assert.IsNotNull(store.Load(b.Id));
            store.RemoveExpired(DateTime.UtcNow + BatchStore.MaxAge + TimeSpan.FromMinutes(1));
            Assert.IsNull(store.Load(b.Id));
        }

        // ---- publishing ----

        [TestMethod]
        public void Publish_ReplacesOnlyTheChosenFiles_InOneStep()
        {
            var b = Mixed();
            File.WriteAllText(Path.Combine(_live, "C.xml"), German("C", "C", extra: "<!-- old -->"));
            var publisher = new LanguagePublisher(_live, Path.Combine(_root, "test.lock"));
            var outcomes = publisher.Publish(b, new HashSet<string> { "A.xml", "C.xml" }, Path.Combine(_root, "batch", "files"));

            Assert.IsTrue(outcomes.Single(o => o.FileName == "A.xml").Published);
            Assert.IsTrue(outcomes.Single(o => o.FileName == "C.xml").Published);
            Assert.AreEqual("not selected", outcomes.Single(o => o.FileName == "B.xml").Reason);
            Assert.AreEqual("cannot be uploaded", outcomes.Single(o => o.FileName == "D.xml").Reason);
            Assert.AreEqual(German("C", "C"), File.ReadAllText(Path.Combine(_live, "C.xml")));
            Assert.IsTrue(File.Exists(Path.Combine(_live, "A.xml")));
            Assert.IsFalse(File.Exists(Path.Combine(_live, "B.xml")));
            Assert.IsFalse(Directory.GetFiles(_live, "*.tmp", SearchOption.AllDirectories).Any(), "no temporary file is left behind");
        }

        [TestMethod]
        public void Publish_ChecksAgain_AgainstWhatIsLiveNow()
        {
            var b = Build(Entry("German.xml", German()));
            // since the upload, another file took the language
            File.WriteAllText(Path.Combine(_live, "Deutsch.xml"), German());
            var outcome = new LanguagePublisher(_live, Path.Combine(_root, "test.lock"))
                .Publish(b, new HashSet<string> { "German.xml" }, Path.Combine(_root, "batch", "files")).Single();
            Assert.IsFalse(outcome.Published);
            StringAssert.Contains(outcome.Reason, "Deutsch.xml");
            Assert.IsFalse(File.Exists(Path.Combine(_live, "German.xml")));
        }

        [TestMethod]
        public void DefaultLock_IsBesideTheLanguageFolder_NotInIt()
        {
            var path = LanguagePublisher.DefaultLockPath(_live + Path.DirectorySeparatorChar);
            Assert.AreEqual(_root, Path.GetDirectoryName(path));
            Assert.AreEqual(".enforcer-languages.lock", Path.GetFileName(path), "the lock the old upload used, so both editions share it");
        }
    }
}
