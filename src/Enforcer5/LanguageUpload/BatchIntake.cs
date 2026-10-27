using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace Enforcer5.LanguageUpload
{
    /// <summary>One file taken from an upload: its content, or why it was not taken.</summary>
    internal class IntakeEntry
    {
        public string FileName { get; set; }
        public byte[] Content { get; set; }
        /// <summary>Why the file cannot be used (null when it can).</summary>
        public string Rejected { get; set; }
    }

    /// <summary>
    /// Turns an uploaded document (one .xml, or a .zip of them) into files. A zip only gives up .xml files at its
    /// top level, under a plain file name, within the size limits; everything else is reported, not unpacked.
    /// </summary>
    internal static class BatchIntake
    {
        public const int MaxFiles = 60;
        public const int MaxFileBytes = 2 * 1024 * 1024;
        public const long MaxTotalBytes = 40L * 1024 * 1024;

        public static bool IsZip(string uploadName) => uploadName?.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) == true;
        public static bool IsXml(string uploadName) => uploadName?.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) == true;

        /// <summary>
        /// A plain .xml file name: no folder, nothing a path could be built from, not hidden. The name is what the
        /// file replaces live, so it is used as it is, never changed.
        /// </summary>
        public static bool IsPlainXmlName(string name) =>
            !string.IsNullOrWhiteSpace(name)
            && IsXml(name)
            && name.Length <= 120
            && name == Path.GetFileName(name)
            && !name.StartsWith(".")
            && name.IndexOfAny(new[] { '/', '\\', ':' }) < 0
            && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

        /// <summary>The files in an upload. Throws <see cref="InvalidDataException"/> if it is not a usable zip or xml.</summary>
        public static List<IntakeEntry> Read(string uploadName, Stream content)
        {
            if (IsXml(uploadName))
            {
                var entry = new IntakeEntry { FileName = uploadName };
                if (IsPlainXmlName(uploadName))
                    Fill(entry, content);
                else
                    entry.Rejected = "the file name cannot be used";
                return new List<IntakeEntry> { entry };
            }
            if (!IsZip(uploadName))
                throw new InvalidDataException("Upload an .xml file or a .zip of them.");

            ZipArchive zip;
            try { zip = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true); }
            catch (InvalidDataException) { throw new InvalidDataException("The zip cannot be opened."); }

            using (zip)
            {
                var result = new List<IntakeEntry>();
                long total = 0;
                foreach (var e in zip.Entries)
                {
                    var name = e.FullName;
                    if (name.EndsWith("/") || IsJunk(name))
                        continue; // folders, and what archivers add on their own (macOS resource forks, .DS_Store)
                    if (result.Count >= MaxFiles)
                        throw new InvalidDataException($"The zip has more than {MaxFiles} files. Split it up.");

                    var entry = new IntakeEntry { FileName = name };
                    result.Add(entry);
                    if (!IsXml(name)) { entry.Rejected = "not an .xml file"; continue; }
                    if (name.Contains("/") || name.Contains("\\")) { entry.Rejected = "in a folder: put the files at the top of the zip"; continue; }
                    if (!IsPlainXmlName(name)) { entry.Rejected = "the file name cannot be used"; continue; }
                    if (result.Count(x => string.Equals(x.FileName, name, StringComparison.OrdinalIgnoreCase)) > 1)
                    { entry.Rejected = "in the zip twice"; continue; }

                    using (var s = e.Open())
                        Fill(entry, s); // the sizes in the zip's directory are not trusted: count what actually comes out
                    total += entry.Content?.Length ?? 0;
                    if (total > MaxTotalBytes)
                        throw new InvalidDataException($"The zip unpacks to more than {MaxTotalBytes / 1024 / 1024} MB.");
                }
                if (result.Count == 0)
                    throw new InvalidDataException("The zip has no files in it.");
                return result;
            }
        }

        private static bool IsJunk(string name) =>
            name.StartsWith("__MACOSX/") || Path.GetFileName(name).StartsWith("._") || Path.GetFileName(name) == ".DS_Store";

        private static void Fill(IntakeEntry entry, Stream s)
        {
            using (var ms = new MemoryStream())
            {
                var buffer = new byte[81920];
                int read;
                while ((read = s.Read(buffer, 0, buffer.Length)) > 0)
                {
                    if (ms.Length + read > MaxFileBytes)
                    {
                        entry.Rejected = $"larger than {MaxFileBytes / 1024 / 1024} MB";
                        return;
                    }
                    ms.Write(buffer, 0, read);
                }
                entry.Content = ms.ToArray();
            }
        }
    }
}
