using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Enforcer5.Enforcement;

namespace Enforcer5.Data
{
    /// <summary>
    /// The enforcement queue's journal: every job not yet carried out, so a restart or a deploy in
    /// the middle of a raid loses none of them. A job is written before its submitter hears it is
    /// queued and removed once it is done or refused for good.
    /// </summary>
    internal interface IEnforcementJournal
    {
        Task SaveAsync(EnforcementJob job);

        Task RemoveAsync(string jobId);

        /// <summary>Every job in the journal; ones that cannot be read are dropped and reported.</summary>
        Task<IReadOnlyList<EnforcementJob>> LoadAsync();
    }
}
