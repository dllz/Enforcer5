using System.Threading.Tasks;
using Enforcer5.Models;

namespace Enforcer5.Data
{
    /// <summary>
    /// Per-user warn counters, written only by the warn actions (<c>Actions/WarnActions</c>) and the data
    /// recovery.
    /// </summary>
    internal interface IWarnRepository
    {
        /// <summary>Current count, 0 if the user has none.</summary>
        Task<long> GetCountAsync(long chatId, WarnKind kind, long userId);

        /// <summary>Takes one warn off, never going below 0. Returns the count left.</summary>
        Task<long> RemoveOneAsync(long chatId, WarnKind kind, long userId);

        /// <summary>Clears the count.</summary>
        Task ResetAsync(long chatId, WarnKind kind, long userId);

        /// <summary>Adds <paramref name="by"/> and returns the count after.</summary>
        Task<long> AddAsync(long chatId, WarnKind kind, long userId, long by);

        /// <summary>Adds <paramref name="by"/> to the member's lifetime warns here (/user) and returns it.</summary>
        Task<long> AddLifetimeAsync(long chatId, long userId, long by);
    }
}
