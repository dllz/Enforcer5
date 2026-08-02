using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Staged-update support for DeployBot. DeployBot drops a new build into Update/, and this
    /// applies it and exits; systemd (Restart=always) brings the new version back up.
    /// Mirrors the mechanism used by blackwolf's Werewolf Control.
    /// </summary>
    internal static class Updater
    {
        private static string UpdateDirectory => Path.Combine(AppContext.BaseDirectory, "Update");

        /// <summary>
        /// Applies a pending update if one is staged, then exits. Call this first thing in Main,
        /// before anything connects to Redis or Telegram.
        /// </summary>
        internal static void ApplyPendingUpdate()
        {
            var updateDirectory = UpdateDirectory;
            try
            {
                if (!Directory.Exists(updateDirectory)) return;
                var files = Directory.GetFiles(updateDirectory);
                if (files.Length == 0) return;

                if (!IsUpdateStable(updateDirectory))
                {
                    // DeployBot is still writing files (docker cp is not atomic). Leave the
                    // staging dir alone and boot the current build; MonitorUpdates will retry
                    // once the copy has finished.
                    Console.Error.WriteLine("[INFO] Pending update still being written, deferring.");
                    return;
                }

                Console.Error.WriteLine($"[INFO] Found pending update with {files.Length} files, applying...");

                foreach (var file in files)
                {
                    var destFile = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(file));
                    File.Copy(file, destFile, overwrite: true);
                }

                foreach (var dir in Directory.GetDirectories(updateDirectory))
                {
                    var destDir = Path.Combine(AppContext.BaseDirectory, Path.GetFileName(dir));
                    CopyDirectory(dir, destDir);
                }

                Directory.Delete(updateDirectory, true);

                Console.Error.WriteLine("[INFO] Update applied, restarting...");
                Environment.Exit(0);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[ERROR] Failed to apply update: {e.Message}\n{e.StackTrace}");
                // Clear the staging dir so we do not loop on a bad update.
                try { Directory.Delete(updateDirectory, true); } catch { }
            }
        }

        /// <summary>
        /// Watches for an update staged while we are running, and exits so it gets applied on
        /// the next start. Run on a background thread.
        /// </summary>
        internal static void MonitorUpdates()
        {
            while (true)
            {
                Thread.Sleep(5000);
                try
                {
                    if (Directory.Exists(UpdateDirectory) && Directory.GetFiles(UpdateDirectory).Length > 0 &&
                        IsUpdateStable(UpdateDirectory))
                    {
                        Console.Error.WriteLine("[INFO] Update detected while running, shutting down to apply...");
                        Bot.Running = false;
                        Program.Running = false;
                        Bot.StopReceiving();
                        Thread.Sleep(1000);
                        Environment.Exit(0);
                    }
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"[ERROR] Update monitor: {e.Message}");
                }
            }
        }

        /// <summary>
        /// DeployBot's docker cp writes staged files one at a time, not atomically. Returns true
        /// only if the directory's file count and total size are unchanged across a short delay,
        /// so we never copy a file that is still being written.
        /// </summary>
        private static bool IsUpdateStable(string updateDirectory)
        {
            var before = SnapshotUpdate(updateDirectory);
            Thread.Sleep(1000);
            var after = SnapshotUpdate(updateDirectory);
            return before == after;
        }

        private static (int Count, long TotalBytes) SnapshotUpdate(string updateDirectory)
        {
            var files = Directory.GetFiles(updateDirectory, "*", SearchOption.AllDirectories);
            var totalBytes = files.Sum(f => new FileInfo(f).Length);
            return (files.Length, totalBytes);
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);
            foreach (var dir in Directory.GetDirectories(sourceDir))
                CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)));
        }
    }
}
