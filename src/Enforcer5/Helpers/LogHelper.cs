using System;
using System.Collections.Concurrent;
using System.IO;

namespace Enforcer5.Helpers
{
    /// <summary>
    /// Minimal rotating file log. Console.Error goes to journald; this is the on-disk copy under
    /// LogPath, which is a small tmpfs on the server - hence the hard size cap.
    /// </summary>
    public static class LogHelper
    {
        private static readonly ConcurrentDictionary<string, object> _locks = new ConcurrentDictionary<string, object>();
        private const long MaxFileSize = 10 * 1024 * 1024; // 10MB per file

        public static void AppendLog(string filePath, string message)
        {
            try
            {
                var lockObj = _locks.GetOrAdd(Path.GetFullPath(filePath), _ => new object());
                lock (lockObj)
                {
                    var dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                        Directory.CreateDirectory(dir);

                    if (File.Exists(filePath))
                    {
                        var info = new FileInfo(filePath);
                        if (info.Length > MaxFileSize)
                        {
                            var oldPath = filePath + ".old";
                            if (File.Exists(oldPath))
                                File.Delete(oldPath);
                            File.Move(filePath, oldPath);
                        }
                    }

                    File.AppendAllText(filePath, message);
                }
            }
            catch
            {
                // Logging must never crash the bot.
            }
        }

        /// <summary>
        /// Writing to stderr can itself throw - it is a journald socket on the server, and that
        /// pipe can break. These must be total: they are called from catch blocks and from the
        /// update consumers, where a throwing logger would silently kill the caller.
        /// </summary>
        public static void Error(string message)
        {
            var line = $"[ERROR] {DateTime.UtcNow:u} {message}";
            try { Console.Error.WriteLine(line); } catch { }
            // Resolving LogDirectory can throw on a malformed LogPath, so it stays inside the
            // guard too - this is called from catch blocks that must not fault.
            try { AppendLog(Path.Combine(Bot.LogDirectory, "error.log"), line + "\n"); } catch { }
        }

        public static void Info(string message)
        {
            try { Console.Error.WriteLine($"[INFO] {DateTime.UtcNow:u} {message}"); } catch { }
        }
    }
}
