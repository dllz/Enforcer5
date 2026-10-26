using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Enforcer5.Enforcement;
using Enforcer5.Helpers;
using StackExchange.Redis;

namespace Enforcer5.Data
{
    /// <summary>
    /// Redis layout: one hash per edition, <c>enforcement:{edition}:jobs</c>, of job id to the job
    /// as JSON. Per edition because each edition carries out its own jobs with its own token. Small
    /// by nature: a job stays only until Telegram has carried it out.
    /// </summary>
    internal sealed class RedisEnforcementJournal : IEnforcementJournal
    {
        private readonly Func<IDatabaseAsync> _database;
        private readonly string _key;

        public RedisEnforcementJournal(Func<IDatabaseAsync> database, string edition)
        {
            _database = database ?? throw new ArgumentNullException(nameof(database));
            _key = $"enforcement:{edition}:jobs";
        }

        internal string Key => _key;

        public Task SaveAsync(EnforcementJob job) =>
            _database().HashSetAsync(_key, job.Id, job.ToJson(), When.Always, CommandFlags.None);

        public Task RemoveAsync(string jobId) =>
            _database().HashDeleteAsync(_key, jobId, CommandFlags.None);

        public async Task<IReadOnlyList<EnforcementJob>> LoadAsync()
        {
            var entries = await _database().HashGetAllAsync(_key, CommandFlags.None);
            var jobs = new List<EnforcementJob>();
            foreach (var entry in entries ?? Array.Empty<HashEntry>())
            {
                try
                {
                    var job = EnforcementJob.FromJson(entry.Value.ToString());
                    if (job != null) jobs.Add(job);
                }
                catch (Exception e)
                {
                    ErrorReporter.Report(e, $"Reading enforcement job {entry.Name}, dropped");
                    await _database().HashDeleteAsync(_key, entry.Name, CommandFlags.None);
                }
            }
            return jobs;
        }
    }
}
