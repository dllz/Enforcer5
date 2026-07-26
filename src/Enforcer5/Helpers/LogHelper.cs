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

        public static void Error(string message)
        {
            Console.Error.WriteLine($"[ERROR] {DateTime.UtcNow:u} {message}");
            AppendLog(Path.Combine(Bot.LogDirectory, "error.log"), $"[ERROR] {DateTime.UtcNow:u} {message}\n");
        }

        public static void Info(string message)
        {
            Console.Error.WriteLine($"[INFO] {DateTime.UtcNow:u} {message}");
        }
    }
}
