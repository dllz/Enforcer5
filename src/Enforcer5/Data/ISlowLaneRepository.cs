using System.Threading.Tasks;
using Enforcer5.Helpers;

namespace Enforcer5.Data
{
    /// <summary>
    /// Who is in the command slow lane (see <see cref="CommandLimiter"/>), kept so a restart does not
    /// let them out and the other edition sees them too.
    /// </summary>
    internal interface ISlowLaneRepository
    {
        /// <summary>The user's stay, or null when there is none.</summary>
        Task<SlowLaneState> GetAsync(long userId);

        Task SaveAsync(long userId, SlowLaneState state);
    }
}
