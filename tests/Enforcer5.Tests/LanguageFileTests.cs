using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Enforcer5.Helpers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Enforcer5.Tests
{
    /// <summary>
    /// English.xml is the master language file: GetLocaleString falls back to it for any key a
    /// translation lacks, and throws when English lacks it too, failing the command. Deployments
    /// never overwrite the live copy on the server, so a key that is missing there stays missing
    /// until someone uploads the file - these checks catch it before that.
    /// </summary>
    [TestClass]
    public class LanguageFileTests
    {
        // Gaps that predate these tests. The checks fail on any new gap, and also when one of
        // these is fixed, so that the entry is removed and the list only ever shrinks. Both were
        // emptied when the repo copy was synced with the live file in October 2026; keep them so.
        private static readonly string[] KnownMissingKeys =
        {
        };

        private static readonly string[] KnownMissingHelp =
        {
        };

        private static XDocument _english;

        [ClassInitialize]
        public static void Load(TestContext context)
        {
            _english = XDocument.Load(TestEnvironment.EnglishXmlPath);
        }

        private static HashSet<string> Keys() =>
            new HashSet<string>(_english.Descendants("string").Select(s => s.Attribute("key")?.Value), StringComparer.Ordinal);

        [TestMethod]
        public void EnglishXml_IsTheEnglishMasterFile()
        {
            var language = _english.Root?.Element("language");
            Assert.IsNotNull(language);
            Assert.AreEqual("English", language.Attribute("name")?.Value);
            Assert.IsTrue(_english.Descendants("string").All(s => !string.IsNullOrEmpty(s.Attribute("key")?.Value)),
                "every string has a key");
        }

        [TestMethod]
        public void EnglishXml_HasNoDuplicateKeys()
        {
            // The lookup in GetLocaleString takes the last match and its English fallback the
            // first, so a duplicate can show different text depending on the path.
            var duplicates = _english.Descendants("string")
                .GroupBy(s => s.Attribute("key")?.Value)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            Assert.AreEqual(0, duplicates.Count, "Duplicate keys: " + string.Join(", ", duplicates));
        }

        [TestMethod]
        public void LiteralKeysUsedInCode_ExistInEnglishXml()
        {
            var keys = Keys();
            var missing = SourceScanner.LiteralLocaleKeys(TestEnvironment.SourceRoot)
                .Where(usage => !keys.Contains(usage.Key))
                .ToList();

            AssertMatchesKnownGaps(
                missing.Select(m => m.Key),
                KnownMissingKeys,
                "Keys used in code but missing from English.xml",
                missing.Select(m => $"{m.Key} ({m.Location})"));
        }

        [TestMethod]
        public void EveryCommand_HasHelpText()
        {
            // /help looks up hcommand{trigger, lowercased}.
            var keys = Keys();
            var missing = typeof(Commands).GetMethods()
                .SelectMany(m => m.GetCustomAttributes<Attributes.Command>())
                .Select(c => c.Trigger.ToLowerInvariant())
                .Where(trigger => !keys.Contains($"hcommand{trigger}"))
                .ToList();

            AssertMatchesKnownGaps(missing, KnownMissingHelp, "Commands without an hcommand help string", missing);
        }

        [DataTestMethod]
        [DataRow("inlineBlockUsage")]
        [DataRow("inlineBlockInvalidUsername", "@1x")]
        [DataRow("inlineBlockInvalidPattern", "Invalid pattern '[' at offset 1.")]
        [DataRow("inlineBlockPatternTooLong", "64")]
        [DataRow("inlineBlockLimit", "50")]
        [DataRow("inlineBlockAdded", "@gif")]
        [DataRow("inlineBlockExists", "@gif")]
        [DataRow("inlineBlockRemoved", "@gif")]
        [DataRow("inlineBlockNotFound", "@gif")]
        [DataRow("inlineBlockList", "@gif\n/^spam/")]
        [DataRow("inlineBlockListEmpty")]
        [DataRow("hcommandblockinline", "blockinline")]
        [DataRow("hcommandunblockinline", "unblockinline")]
        [DataRow("hcommandblockedinline", "blockedinline")]
        public void InlineBlockStrings_FormatWithTheirArguments(string key, params string[] args)
        {
            var text = Methods.GetLocaleString(_english, key, args.Cast<object>().ToArray());
            Assert.IsFalse(string.IsNullOrWhiteSpace(text));
            foreach (var arg in args)
            {
                Assert.IsTrue(text.Contains(arg.FormatHTML()), $"{key} shows its argument");
            }
        }

        [TestMethod]
        public void SourceScanner_FindsLiteralKeysOnly()
        {
            const string code = @"
                Methods.GetLocaleString(lang, ""plain"", x);
                Methods.GetLocaleString(Methods.GetGroupLanguage(m, true).Doc, ""afterNestedCall"");
                Methods.GetLocaleString(lang, ok ? ""whenTrue"" : ""whenFalse"", block);
                Methods.GetLocaleString(lang, $""hcommand{request}"", request);
                Methods.GetLocaleString(lang, key);
                Methods.GetLocaleStringNoFormat(doc, @""verbatim"");
                Methods.GetLocaleString(lang, ""escaped\""quote"", ',', '""');
                public static string GetLocaleString(XDocument file, string key, params object[] args)
                Methods.GetLocaleString(lang, ""last"");";

            var keys = SourceScanner.Scan(code).Select(k => k.Key).ToList();
            CollectionAssert.AreEquivalent(
                new[] { "plain", "afterNestedCall", "whenTrue", "whenFalse", "verbatim", "escaped\\\"quote", "last" },
                keys);
        }

        private static void AssertMatchesKnownGaps(IEnumerable<string> found, string[] known, string what, IEnumerable<string> details)
        {
            var foundSet = new HashSet<string>(found, StringComparer.Ordinal);
            var added = foundSet.Except(known).OrderBy(x => x, StringComparer.Ordinal).ToList();
            var fixedOnes = known.Except(foundSet).OrderBy(x => x, StringComparer.Ordinal).ToList();

            if (added.Count > 0)
            {
                var lines = details.Where(d => added.Any(a => d.StartsWith(a, StringComparison.Ordinal))).Distinct();
                Assert.Fail($"{what}:\n{string.Join("\n", lines)}");
            }
            if (fixedOnes.Count > 0)
            {
                Assert.Fail($"No longer missing, remove from the known list: {string.Join(", ", fixedOnes)}");
            }
        }
    }

    /// <summary>
    /// Finds the string literals in the key argument of GetLocaleString / GetLocaleStringNoFormat
    /// calls, including both arms of a conditional like <c>ok ? "keyA" : "keyB"</c>. Interpolated
    /// and computed keys cannot be checked statically and are skipped. A deliberately small
    /// scanner rather than a C# parser: it handles the shapes this codebase uses.
    /// </summary>
    internal static class SourceScanner
    {
        internal readonly struct Usage
        {
            public Usage(string key, string location) { Key = key; Location = location; }
            public string Key { get; }
            public string Location { get; }
        }

        private static readonly string[] Methods = { "GetLocaleStringNoFormat(", "GetLocaleString(" };

        internal static IEnumerable<Usage> LiteralLocaleKeys(string sourceRoot)
        {
            var files = Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                .Where(f => !IsBuildOutput(sourceRoot, f));
            foreach (var file in files)
            {
                var text = File.ReadAllText(file);
                foreach (var (key, offset) in Scan(text))
                {
                    var line = text.Take(offset).Count(c => c == '\n') + 1;
                    yield return new Usage(key, $"{Path.GetRelativePath(sourceRoot, file)}:{line}");
                }
            }
        }

        /// <summary>Literal keys in <paramref name="text"/>, with the offset of their call.</summary>
        internal static IEnumerable<(string Key, int Offset)> Scan(string text)
        {
            foreach (var method in Methods)
            {
                for (var at = text.IndexOf(method, StringComparison.Ordinal); at >= 0;
                     at = text.IndexOf(method, at + method.Length, StringComparison.Ordinal))
                {
                    // Part of a longer identifier, e.g. a hypothetical TryGetLocaleString(.
                    if (at > 0 && (char.IsLetterOrDigit(text[at - 1]) || text[at - 1] == '_')) continue;

                    var endOfFirst = ReadArgument(text, at + method.Length, null);
                    if (endOfFirst < 0 || text[endOfFirst] != ',') continue; // only one argument

                    var keys = new List<string>();
                    if (ReadArgument(text, endOfFirst + 1, keys) < 0) continue;
                    foreach (var key in keys) yield return (key, at);
                }
            }
        }

        private static bool IsBuildOutput(string root, string file)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            return relative.StartsWith("bin/", StringComparison.Ordinal) || relative.StartsWith("obj/", StringComparison.Ordinal);
        }

        /// <summary>
        /// Reads one call argument starting at <paramref name="i"/>, adding the non-interpolated
        /// string literals in it to <paramref name="literals"/>. Returns the index of the ',' or ')'
        /// that ends it, or -1.
        /// </summary>
        private static int ReadArgument(string text, int i, List<string> literals)
        {
            var depth = 0;
            for (; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '"')
                {
                    var prefix = i >= 2 ? text.Substring(i - 2, 2) : text.Substring(0, i);
                    var interpolated = prefix.EndsWith("$") || prefix == "$@";
                    var verbatim = prefix.EndsWith("@") || prefix == "@$";
                    var end = EndOfString(text, i, verbatim, interpolated);
                    if (end < 0) return -1;
                    if (!interpolated) literals?.Add(text.Substring(i + 1, end - i - 1));
                    i = end;
                }
                else if (c == '\'')
                {
                    // Char literal, which may be '"' or an escape such as '\''.
                    var close = text.IndexOf('\'', i + 2);
                    if (text[i + 1] == '\\' && close == i + 2) close = text.IndexOf('\'', i + 3);
                    if (close < 0) return -1;
                    i = close;
                }
                else if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}')
                {
                    if (depth == 0) return i;
                    depth--;
                }
                else if (c == ',' && depth == 0) return i;
            }
            return -1;
        }

        /// <summary>Index of the quote closing the string opened at <paramref name="open"/>, or -1.</summary>
        private static int EndOfString(string text, int open, bool verbatim, bool interpolated)
        {
            var holes = 0;
            for (var j = open + 1; j < text.Length; j++)
            {
                var c = text[j];
                if (holes > 0)
                {
                    // Inside an interpolation hole: code, which may contain strings of its own.
                    if (c == '"')
                    {
                        j = EndOfString(text, j, text[j - 1] == '@', text[j - 1] == '$');
                        if (j < 0) return -1;
                    }
                    else if (c == '{') holes++;
                    else if (c == '}') holes--;
                    continue;
                }
                if (!verbatim && c == '\\')
                {
                    j++;
                    continue;
                }
                if (c == '"')
                {
                    if (verbatim && j + 1 < text.Length && text[j + 1] == '"')
                    {
                        j++;
                        continue;
                    }
                    return j;
                }
                if (interpolated && c == '{')
                {
                    if (j + 1 < text.Length && text[j + 1] == '{') j++;
                    else holes++;
                }
            }
            return -1;
        }
    }
}
