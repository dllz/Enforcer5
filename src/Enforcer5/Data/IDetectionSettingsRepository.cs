using System.Threading.Tasks;
using Enforcer5.Detection;

namespace Enforcer5.Data
{
    /// <summary>
    /// Where each group's <see cref="DetectionPolicy"/> is kept: PostgreSQL (<c>group_settings</c>,
    /// <c>detection_policies</c>, <c>group_filters</c>). Read through <see cref="DetectionSettings"/>,
    /// which caches it; nothing else should call this.
    /// </summary>
    internal interface IDetectionSettingsRepository
    {
        /// <summary>The group's settings, or null when they were never imported.</summary>
        Task<DetectionPolicy> LoadAsync(long chatId);

        /// <summary>
        /// Stores <paramref name="policy"/> as the group's first settings, at version 1. When the
        /// group already has settings (another process imported them first), those are returned
        /// unchanged.
        /// </summary>
        Task<DetectionPolicy> ImportAsync(long chatId, DetectionPolicy policy);

        /// <summary>
        /// Replaces the group's settings with <paramref name="policy"/> if they are still at
        /// <paramref name="expectedVersion"/>, and returns them with the new version; null when
        /// someone changed them in the meantime.
        /// </summary>
        Task<DetectionPolicy> SaveAsync(long chatId, DetectionPolicy policy, long expectedVersion);
    }
}
