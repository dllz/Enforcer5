using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

namespace Enforcer5.LanguageUpload
{
    /// <summary>
    /// Keeps uploads waiting for a decision on disk, so they survive a restart and the menu can be opened again:
    /// <c>{root}/{id}/batch.json</c> and the uploaded files in <c>{root}/{id}/files/</c>. Changes to one batch are
    /// made one at a time (two admins may tap at once).
    /// </summary>
    internal class BatchStore
    {
        public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
        private static readonly Regex IdFormat = new Regex("^[0-9a-f]{12}$", RegexOptions.Compiled);
        private static readonly ConcurrentDictionary<string, object> Locks = new ConcurrentDictionary<string, object>();

        private readonly string _root;

        public BatchStore(string root) => _root = root;

        public static string NewId() => Guid.NewGuid().ToString("N").Substring(0, 12);

        /// <summary>Ids come back in callback data, which a client can forge: only this format ever becomes a path.</summary>
        public static bool IsValidId(string id) => id != null && IdFormat.IsMatch(id);

        public string FilesDirectory(string id) => Path.Combine(BatchDirectory(id), "files");

        private string BatchDirectory(string id) =>
            IsValidId(id) ? Path.Combine(_root, id) : throw new ArgumentException("Invalid batch id", nameof(id));

        private string JsonPath(string id) => Path.Combine(BatchDirectory(id), "batch.json");

        public void Save(LanguageBatch batch)
        {
            var path = JsonPath(batch.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(batch, Formatting.Indented));
            File.Move(temp, path, true);
        }

        /// <summary>The batch, or null if there is none with that id (expired, or never was).</summary>
        public LanguageBatch Load(string id)
        {
            if (!IsValidId(id)) return null;
            var path = JsonPath(id);
            if (!File.Exists(path)) return null;
            try { return JsonConvert.DeserializeObject<LanguageBatch>(File.ReadAllText(path)); }
            catch (JsonException) { return null; }
        }

        /// <summary>
        /// Loads the batch, lets <paramref name="change"/> work on it and saves it, while no other change to the same
        /// batch runs. <paramref name="change"/> gets null if the batch does not exist (and nothing is saved).
        /// </summary>
        public T Change<T>(string id, Func<LanguageBatch, T> change)
        {
            lock (Locks.GetOrAdd(id ?? "", _ => new object()))
            {
                var batch = Load(id);
                var result = change(batch);
                if (batch != null) Save(batch);
                return result;
            }
        }

        /// <summary>Removes the uploaded files once a batch is decided; its record stays until it expires.</summary>
        public void DeleteFiles(string id)
        {
            var dir = FilesDirectory(id);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        /// <summary>Removes batches older than <see cref="MaxAge"/>.</summary>
        public void RemoveExpired(DateTime nowUtc)
        {
            if (!Directory.Exists(_root)) return;
            foreach (var dir in Directory.GetDirectories(_root))
            {
                var id = Path.GetFileName(dir);
                if (!IsValidId(id)) continue;
                var created = Load(id)?.CreatedUtc ?? Directory.GetCreationTimeUtc(dir);
                if (nowUtc - created <= MaxAge) continue;
                try
                {
                    Directory.Delete(dir, true);
                    Locks.TryRemove(id, out _);
                }
                catch (IOException) { /* in use: next time */ }
            }
        }
    }
}
