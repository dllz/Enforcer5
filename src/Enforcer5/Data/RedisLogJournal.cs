using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Enforcer5.Helpers;
using StackExchange.Redis;

namespace Enforcer5.Data
{
    /// <summary>One log channel post waiting to go out (<see cref="LogQueue"/>).</summary>
    internal sealed record LogEntry(string Id, long Sequence, long GroupId, long LogChat, string Text, string LinkUrl, string LinkLabel);

    /// <summary>
    /// The log queue's journal: every log channel post not yet sent, so a restart or a deploy loses none.
    /// Written when the entry is queued, removed once it is sent or given up on.
    /// </summary>
    internal interface ILogJournal
    {
        void Save(LogEntry entry);

        void Remove(IEnumerable<string> ids);

        /// <summary>Every entry, oldest first; ones that cannot be read are dropped.</summary>
        Task<IReadOnlyList<LogEntry>> LoadAsync();
    }

    /// <summary>
    /// Redis layout: one hash per edition, <c>logqueue:{edition}</c>, of entry id to the entry as JSON. Per
    /// edition because each edition posts with its own token. Small by nature: an entry stays only until it
    /// is posted. Writes are fire and forget, so queueing a log line never waits on Redis.
    /// </summary>
    internal sealed class RedisLogJournal : ILogJournal
    {
        private readonly Func<IDatabaseAsync> _database;

        public RedisLogJournal(Func<IDatabaseAsync> database, string edition)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            Key = $"logqueue:{edition}";
        }

        internal string Key { get; }

        public void Save(LogEntry entry) =>
            _ = _database().HashSetAsync(Key, entry.Id, JsonSerializer.Serialize(entry), When.Always, CommandFlags.FireAndForget);

        public void Remove(IEnumerable<string> ids)
        {
            var fields = ids.Select(id => (RedisValue)id).ToArray();
            if (fields.Length > 0) _ = _database().HashDeleteAsync(Key, fields, CommandFlags.FireAndForget);
        }

        public async Task<IReadOnlyList<LogEntry>> LoadAsync()
        {
            var entries = await _database().HashGetAllAsync(Key, CommandFlags.None);
            var list = new List<LogEntry>();
            foreach (var entry in entries ?? Array.Empty<HashEntry>())
            {
                try
                {
                    var read = JsonSerializer.Deserialize<LogEntry>(entry.Value.ToString());
                    if (read?.Text != null) list.Add(read);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Reading log queue entry {entry.Name}, dropped");
                    _ = _database().HashDeleteAsync(Key, entry.Name, CommandFlags.FireAndForget);
                }
            }
            return list.OrderBy(e => e.Sequence).ToList();
        }
    }
}
