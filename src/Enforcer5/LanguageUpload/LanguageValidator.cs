using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Enforcer5.Handlers;
using Enforcer5.Models;

namespace Enforcer5.LanguageUpload
{
    /// <summary>
    /// Everything a language file is checked for: its language node, the language selector's callback length, the
    /// per-string checks against English (the master file), and clashes with other files. Used by uploads
    /// (<see cref="BatchBuilder"/>, <see cref="LanguagePublisher"/>) and /validatelanguages. Ported from blackwolf's
    /// LanguageValidator with Enforcer's own rules (no variants, no /join or @username checks).
    /// </summary>
    internal static class LanguageValidator
    {
        internal const string LanguageNodeKey = "Language Node";

        /// <summary>The file's own problems (not clashes with other files; see <see cref="FindConflict"/>).</summary>
        internal static List<LanguageError> Check(Language file, XDocument master)
        {
            var errors = new List<LanguageError>();
            CheckLanguageNode(file, errors);
            TestLength(file, errors);
            errors.AddRange(CheckStrings(file.FileName, file.Doc, master));
            return errors;
        }

        /// <summary>A fatal problem: the file cannot be uploaded, whatever an admin accepts.</summary>
        internal static bool IsFatal(LanguageError error) => error.Level == ErrorLevel.FatalError;

        /// <summary>
        /// The first of <paramref name="others"/> the candidate clashes with, or null. A file replaces another only
        /// under the same file name, and the language selector tells files apart by base, so these are clashes: the
        /// same file name (any case) with another language name; the same language name with another base; the same
        /// base under another file name.
        /// </summary>
        internal static Language FindConflict(Language candidate, IEnumerable<Language> others) =>
            others.FirstOrDefault(x =>
                (string.Equals(x.FileName, candidate.FileName, StringComparison.OrdinalIgnoreCase) && x.Name != candidate.Name)
                || (x.Name == candidate.Name && x.Base != candidate.Base)
                || (x.Base == candidate.Base && x.FileName != candidate.FileName));

        internal static LanguageError ConflictError(Language candidate, Language conflict) =>
            new LanguageError(candidate.FileName, LanguageNodeKey,
                $"Clashes with {conflict.FileName}.xml (name {conflict.Name}, base {conflict.Base}). " +
                "Check the file name and the language name and base.", ErrorLevel.FatalError);

        /// <summary>The per-string checks against English.</summary>
        internal static List<LanguageError> CheckStrings(string fileName, XDocument file, XDocument master)
        {
            var errors = new List<LanguageError>();
            var strings = file.Descendants("string").Where(s => Key(s) != null).ToList();

            // The readers do not agree on which copy of a key they use, so a second copy is a warning either way.
            foreach (var key in strings.GroupBy(Key).Where(g => g.Count() > 1).Select(g => g.Key))
                errors.Add(new LanguageError(fileName, key, "in the file twice", ErrorLevel.DuplicatedString));

            var byKey = strings.GroupBy(Key).ToDictionary(g => g.Key, g => g.First());
            foreach (var str in master.Descendants("string"))
            {
                var key = Key(str);
                if (key == null) continue;
                if (!byKey.TryGetValue(key, out var translated) || !translated.Descendants("value").Any())
                {
                    if (str.Attribute("deprecated") == null)
                        errors.Add(new LanguageError(fileName, key, "Values missing"));
                    continue;
                }

                var english = str.Descendants("value").FirstOrDefault()?.Value ?? "";
                var placeholders = 0;
                for (var i = 0; i < 5; i++)
                    if (english.Contains("{" + i + "}")) placeholders = i + 1;
                var isGif = str.Attribute("isgif") != null;

                foreach (var value in translated.Descendants("value").Select(v => v.Value))
                {
                    for (var i = 0; i < 5; i++)
                    {
                        var has = value.Contains("{" + i + "}");
                        if (!has && i < placeholders)
                            errors.Add(new LanguageError(fileName, key, "Missing {" + i + "}", ErrorLevel.Error));
                        else if (has && i >= placeholders)
                            errors.Add(new LanguageError(fileName, key, "Extra {" + i + "}", ErrorLevel.Error));
                    }
                    if (isGif && value.Length > 200)
                        errors.Add(new LanguageError(fileName, key, "longer than 200 characters", ErrorLevel.GifTooLong));
                }
            }
            return errors;
        }

        private static string Key(XElement str) => (string)str.Attribute("key");

        private static void CheckLanguageNode(Language file, List<LanguageError> errors)
        {
            if (string.IsNullOrWhiteSpace(file.Name))
                errors.Add(new LanguageError(file.FileName, LanguageNodeKey, "Language name is missing", ErrorLevel.FatalError));
            if (string.IsNullOrWhiteSpace(file.Base))
                errors.Add(new LanguageError(file.FileName, LanguageNodeKey, "Base is missing", ErrorLevel.FatalError));
        }

        private static void TestLength(Language file, List<LanguageError> errors)
        {
            // The language selector puts the base in its callback data, which Telegram limits to 64 bytes.
            var test = $"setlang|-1001049529775|{file.Base ?? ""}|v";
            if (Encoding.UTF8.GetByteCount(test) > 64)
                errors.Add(new LanguageError(file.FileName, LanguageNodeKey, "Base is too long for the language selector (38 UTF-8 bytes at most).", ErrorLevel.FatalError));
        }
    }
}
