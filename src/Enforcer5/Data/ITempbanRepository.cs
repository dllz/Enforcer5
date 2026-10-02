using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Enforcer5.Models;

namespace Enforcer5.Data
{
    /// <summary>
    /// Reads the tempbans running in a chat. Read-only for now: Commands.Tempban and the expiry
    /// checker still write the legacy keys directly, and move here when that code is next touched.
    /// </summary>
    internal interface ITempbanRepository
    {
        /// <summary>Tempbans in the chat that end after <paramref name="nowUtc"/>, one per user.</summary>
        Task<IReadOnlyList<TimedRestriction>> GetActiveAsync(long chatId, DateTime nowUtc);
    }
}
