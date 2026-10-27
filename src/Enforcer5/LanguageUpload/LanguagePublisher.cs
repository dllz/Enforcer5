using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Enforcer5.Helpers;

namespace Enforcer5.LanguageUpload
{
    /// <summary>What happened to one file when a batch was decided.</summary>
    internal class PublishOutcome
    {
        public string FileName { get; set; }
        public bool Published { get; set; }
        /// <summary>Why it was not published (null when it was).</summary>
        public string Reason { get; set; }
    }

    /// <summary>
    /// Puts the chosen files of a batch live. Both editions share one language folder, so only one publish runs at a
    /// time (a lock file next to that folder). Each file is checked again under the lock, since the live files may
    /// have changed since the upload, and replaces its live file in one step: written beside it under a temporary
    /// name, then renamed over it, so neither bot reads half a file. Both pick the change up from the folder.
    /// </summary>
    internal class LanguagePublisher
    {
        private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
        private readonly string _liveDirectory;
        private readonly string _lockPath;
        private readonly string _fallbackLockPath;

        /// <param name="lockPath">Defaults to <see cref="DefaultLockPath"/>, shared by both editions.</param>
        /// <param name="fallbackLockPath">Used if the process may not write <paramref name="lockPath"/>; then only this edition is serialised.</param>
        internal LanguagePublisher(string liveDirectory, string lockPath = null, string fallbackLockPath = null)
        {
            _liveDirectory = liveDirectory;
            _lockPath = lockPath ?? DefaultLockPath(liveDirectory);
            _fallbackLockPath = fallbackLockPath;
        }

        internal static string DefaultLockPath(string liveDirectory)
        {
            var parent = Directory.GetParent(Path.TrimEndingDirectorySeparator(Path.GetFullPath(liveDirectory)))?.FullName ?? liveDirectory;
            return Path.Combine(parent, ".enforcer-languages.lock");
        }

        /// <summary>Publishes <paramref name="chosen"/> (file names in the batch) from <paramref name="filesDirectory"/>.</summary>
        internal List<PublishOutcome> Publish(LanguageBatch batch, ICollection<string> chosen, string filesDirectory)
        {
            var outcomes = new List<PublishOutcome>();
            using (AcquireLock())
            {
                var master = XDocument.Load(Path.Combine(_liveDirectory, "English.xml"));
                foreach (var file in batch.Files)
                {
                    if (!chosen.Contains(file.FileName))
                    {
                        outcomes.Add(new PublishOutcome { FileName = file.FileName, Reason = file.IsFatal ? "cannot be uploaded" : "not selected" });
                        continue;
                    }
                    outcomes.Add(PublishOne(file, filesDirectory, master));
                }
            }
            return outcomes;
        }

        private PublishOutcome PublishOne(BatchFile file, string filesDirectory, XDocument master)
        {
            var outcome = new PublishOutcome { FileName = file.FileName };
            try
            {
                var source = Path.Combine(filesDirectory, file.FileName);
                var lang = BatchBuilder.TryLoad(source, out var loadError);
                if (lang == null)
                    return Fail(outcome, loadError);
                var fatal = LanguageValidator.Check(lang, master).FirstOrDefault(LanguageValidator.IsFatal);
                if (fatal != null)
                    return Fail(outcome, fatal.Message);
                var conflict = LanguageValidator.FindConflict(lang, BatchBuilder.LoadLive(_liveDirectory));
                if (conflict != null)
                    return Fail(outcome, $"now clashes with {conflict.FileName}.xml");

                Replace(source, Path.Combine(_liveDirectory, file.FileName));
                outcome.Published = true;
                return outcome;
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                return Fail(outcome, e.Message);
            }
        }

        private static PublishOutcome Fail(PublishOutcome outcome, string reason)
        {
            outcome.Reason = reason;
            return outcome;
        }

        private static void Replace(string source, string destination)
        {
            var temp = Path.Combine(Path.GetDirectoryName(destination), $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.Copy(source, temp);
                File.Move(temp, destination, true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }

        private FileStream AcquireLock()
        {
            try
            {
                return AcquireLock(_lockPath);
            }
            catch (UnauthorizedAccessException) when (_fallbackLockPath != null)
            {
                LogHelper.Info($"Language publish: cannot write {_lockPath}, locking {_fallbackLockPath} instead");
                Directory.CreateDirectory(Path.GetDirectoryName(_fallbackLockPath));
                return AcquireLock(_fallbackLockPath);
            }
        }

        private static FileStream AcquireLock(string path)
        {
            var deadline = DateTime.UtcNow + LockTimeout;
            while (true)
            {
                try
                {
                    return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                }
                catch (IOException) when (DateTime.UtcNow < deadline)
                {
                    // Real time on purpose: the other holder is the other edition's process, not simulated work.
                    Thread.Sleep(100);
                }
            }
        }
    }
}
