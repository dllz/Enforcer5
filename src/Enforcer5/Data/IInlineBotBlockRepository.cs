using System.Collections.Generic;
using System.Threading.Tasks;
using Enforcer5.Models;

namespace Enforcer5.Data
{
    /// <summary>
    /// Persistence for each chat's inline bot blocklist. Callers depend on this rather than on a
    /// store, so the backing database can change without touching them.
    /// </summary>
    internal interface IInlineBotBlockRepository
    {
        /// <summary>All entries for the chat, in no particular order. Empty if there are none.</summary>
        Task<IReadOnlyList<InlineBotBlock>> GetAsync(long chatId);

        /// <summary>Number of entries for the chat.</summary>
        Task<long> CountAsync(long chatId);

        /// <summary>Adds the entry. False if the chat already had it.</summary>
        Task<bool> AddAsync(long chatId, InlineBotBlock block);

        /// <summary>Removes the entry. False if the chat did not have it.</summary>
        Task<bool> RemoveAsync(long chatId, InlineBotBlock block);
    }
}
