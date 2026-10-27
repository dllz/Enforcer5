using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Enforcer5.Handlers;
using Enforcer5.Models;

namespace Enforcer5.LanguageUpload
{
    /// <summary>Checks the files of an upload and records the result as a <see cref="LanguageBatch"/>.</summary>
    internal static class BatchBuilder
    {
        internal const string FileKey = "File";

        /// <summary>
        /// Writes the usable files to <paramref name="filesDirectory"/> and checks each one on its own
        /// (<see cref="LanguageValidator.Check"/>), against the live files, and against the other files of the upload.
        /// </summary>
        internal static LanguageBatch Build(string id, long chatId, DateTime nowUtc, IList<IntakeEntry> entries,
            string filesDirectory, string liveDirectory, XDocument master)
        {
            Directory.CreateDirectory(filesDirectory);
            var live = LoadLive(liveDirectory);
            var batch = new LanguageBatch { Id = id, ChatId = chatId, CreatedUtc = nowUtc };
            var loaded = new List<(BatchFile File, Language Lang)>();

            foreach (var entry in entries)
            {
                var file = new BatchFile { FileName = entry.FileName };
                batch.Files.Add(file);
                var stem = Path.GetFileNameWithoutExtension(entry.FileName);
                if (entry.Rejected != null)
                {
                    file.Errors.Add(new LanguageError(stem, FileKey, entry.Rejected, ErrorLevel.FatalError));
                    continue;
                }
                var path = Path.Combine(filesDirectory, entry.FileName);
                File.WriteAllBytes(path, entry.Content);
                var lang = TryLoad(path, out var loadError);
                if (lang == null)
                {
                    file.Errors.Add(new LanguageError(stem, FileKey, loadError, ErrorLevel.FatalError));
                    continue;
                }
                file.Name = lang.Name;
                file.Base = lang.Base;
                file.Errors.AddRange(LanguageValidator.Check(lang, master));
                var conflict = LanguageValidator.FindConflict(lang, live);
                if (conflict != null)
                    file.Errors.Add(LanguageValidator.ConflictError(lang, conflict));

                var current = live.FirstOrDefault(x => x.Name == lang.Name);
                file.IsNewLanguage = current == null;
                if (current != null)
                    file.LiveMissing = LanguageValidator.Check(current, master).Count(e => e.Level == ErrorLevel.MissingString);
                loaded.Add((file, lang));
            }

            // two files of one upload that would land on the same language cannot both go up
            foreach (var (file, lang) in loaded)
            {
                var peer = LanguageValidator.FindConflict(lang, loaded.Where(x => x.File != file).Select(x => x.Lang));
                if (peer != null)
                    file.Errors.Add(LanguageValidator.ConflictError(lang, peer));
            }
            return batch;
        }

        /// <summary>The live language files that can be read; a broken one is skipped, not a reason to stop.</summary>
        internal static List<Language> LoadLive(string liveDirectory) =>
            Directory.GetFiles(liveDirectory, "*.xml").Select(p => TryLoad(p, out _)).Where(x => x != null).ToList();

        /// <summary>The file, or null with the reason it cannot be read as a language file.</summary>
        internal static Language TryLoad(string path, out string error)
        {
            try
            {
                error = null;
                return new Language(path);
            }
            catch (System.Xml.XmlException e)
            {
                error = "not valid XML: " + e.Message;
            }
            catch (InvalidOperationException)
            {
                error = "no <language> element";
            }
            catch (IOException e)
            {
                error = "cannot be read: " + e.Message;
            }
            return null;
        }
    }
}
