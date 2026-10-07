using System.Threading.Tasks;
using Enforcer5.Models;

namespace Enforcer5.Data
{
    /// <summary>
    /// Per-user warn counters. So far only the removal side lives here; issuing warns
    /// (Commands.Warn) still writes Redis directly and moves over when that code is touched.
    /// </summary>
    internal interface IWarnRepository
    {
        /// <summary>Current count, 0 if the user has none.</summary>
        Task<long> GetCountAsync(long chatId, WarnKind kind, long userId);

        /// <summary>Takes one warn off, never going below 0. Returns the count left.</summary>
        Task<long> RemoveOneAsync(long chatId, WarnKind kind, long userId);

        /// <summary>Clears the count.</summary>
        Task ResetAsync(long chatId, WarnKind kind, long userId);
    }
}
