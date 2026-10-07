using System.Collections.Generic;
using System.Threading.Tasks;

namespace Enforcer5.Data
{
    /// <summary>
    /// Persistence for each chat's sticker pack blocklist. Names are the canonical form from
    /// <see cref="Models.StickerSetName"/>; callers normalise before calling.
    /// </summary>
    internal interface IStickerSetBlockRepository
    {
        /// <summary>All blocked pack names for the chat, in no particular order.</summary>
        Task<IReadOnlyList<string>> GetAsync(long chatId);

        /// <summary>Number of blocked packs for the chat.</summary>
        Task<long> CountAsync(long chatId);

        /// <summary>Whether the pack is blocked. One lookup, so it is cheap enough for every sticker.</summary>
        Task<bool> ContainsAsync(long chatId, string setName);

        /// <summary>Blocks the pack. False if it already was.</summary>
        Task<bool> AddAsync(long chatId, string setName);

        /// <summary>Unblocks the pack. False if it was not blocked.</summary>
        Task<bool> RemoveAsync(long chatId, string setName);
    }
}
