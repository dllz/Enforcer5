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

        /// <summary>
        /// What the bot is doing, for reading back after something went wrong without an error (2026-11-05: a queue that
        /// hung logged nothing). To journald and to <c>app.log</c> beside <c>error.log</c> (same rotation: 10 MB and one
        /// <c>.old</c>, so both fit the 50 MB tmpfs). At most <see cref="InfoPerSecond"/> lines a second reach the file; the
        /// next line that gets through says how many were left out.
        /// </summary>
        public static void Info(string message)
        {
            var line = $"[INFO] {DateTime.UtcNow:u} {message}";
            try { Console.Error.WriteLine(line); } catch { }
            try
            {
                int dropped;
                lock (_infoLock)
                {
                    var second = DateTime.UtcNow.Ticks / TimeSpan.TicksPerSecond;
                    if (second != _infoSecond)
                    {
                        _infoSecond = second;
                        _infoCount = 0;
                    }
                    if (++_infoCount > InfoPerSecond)
                    {
                        _infoDropped++;
                        return;
                    }
                    dropped = _infoDropped;
                    _infoDropped = 0;
                }
                if (dropped > 0) line = $"[INFO] {DateTime.UtcNow:u} ({dropped} lines left out)\n" + line;
                AppendLog(Path.Combine(Bot.LogDirectory, "app.log"), line + "\n");
            }
            catch { }
        }

        internal const int InfoPerSecond = 50;
        private static readonly object _infoLock = new object();
        private static long _infoSecond;
        private static int _infoCount;
        private static int _infoDropped;
    }
}
