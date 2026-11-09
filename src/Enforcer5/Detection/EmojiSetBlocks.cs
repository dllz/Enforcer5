using System;
using System.Linq;
using System.Threading.Tasks;

namespace Enforcer5.Detection
{
    /// <summary>
    /// Each group's custom emoji packs (<see cref="DetectionPolicy.EmojiSets"/>): banned by an admin with /block, banned by
    /// Detection as NSFW, or allowed by the admins after that (Daniel, 2026-11-09).
    /// </summary>
    internal static class EmojiSetBlocks
    {
        /// <summary>The most packs admins ban in one group, as for sticker packs: the /blocked list stays one message.</summary>
        internal const int MaxAdmin = 50;

        /// <summary>The most packs Detection keeps banned in one group; the oldest go first.</summary>
        internal const int MaxAuto = 500;

        internal enum Added { Added, Already, Full }

        /// <summary>An admin bans the pack (an NSFW ban or an allowed one becomes theirs).</summary>
        internal static async Task<Added> BlockAsync(long chatId, string setName)
        {
            var result = Added.Added;
            await DetectionSettings.UpdateAsync(chatId, policy =>
            {
                var sets = policy.EmojiSets;
                if (sets.Any(e => e.Name == setName && e.Kind == EmojiSetEntry.Admin))
                {
                    result = Added.Already;
                    return policy;
                }
                if (sets.Count(e => e.Kind == EmojiSetEntry.Admin) >= MaxAdmin)
                {
                    result = Added.Full;
                    return policy;
                }
                result = sets.Any(e => e.Name == setName && e.Blocks) ? Added.Already : Added.Added;
                return policy with
                {
                    EmojiSets = sets.Where(e => e.Name != setName).Append(new EmojiSetEntry(setName, EmojiSetEntry.Admin)).ToList()
                };
            });
            return result;
        }

        /// <summary>
        /// Detection bans the pack as NSFW, unless it is banned already or the admins allowed it. True when it was added.
        /// </summary>
        internal static async Task<bool> BlockNsfwAsync(long chatId, string setName)
        {
            var added = false;
            await DetectionSettings.UpdateAsync(chatId, policy =>
            {
                if (policy.EmojiSets.Any(e => e.Name == setName)) return policy;
                added = true;
                var sets = policy.EmojiSets.Append(new EmojiSetEntry(setName, EmojiSetEntry.Auto)).ToList();
                var over = sets.Count(e => e.Kind == EmojiSetEntry.Auto) - MaxAuto;
                if (over > 0)
                {
                    var oldest = sets.Where(e => e.Kind == EmojiSetEntry.Auto).Take(over).ToHashSet();
                    sets = sets.Where(e => !oldest.Contains(e)).ToList();
                }
                return policy with { EmojiSets = sets };
            });
            return added;
        }

        /// <summary>An admin lifts the ban on the pack, whoever banned it. False when it was not banned.</summary>
        internal static async Task<bool> UnblockAsync(long chatId, string setName)
        {
            var removed = false;
            await DetectionSettings.UpdateAsync(chatId, policy =>
            {
                removed = policy.EmojiSets.Any(e => e.Name == setName && e.Blocks);
                return removed ? policy with { EmojiSets = policy.EmojiSets.Where(e => e.Name != setName).ToList() } : policy;
            });
            return removed;
        }

        /// <summary>
        /// The admins found a report about the pack false: Detection's ban on it is lifted here, and it is not banned here
        /// again for being NSFW. A ban an admin made stays. True when it changed.
        /// </summary>
        internal static async Task<bool> AllowAsync(long chatId, string setName)
        {
            var changed = false;
            await DetectionSettings.UpdateAsync(chatId, policy =>
            {
                if (policy.EmojiSets.Any(e => e.Name == setName && e.Kind is EmojiSetEntry.Admin or EmojiSetEntry.Allowed)) return policy;
                changed = true;
                return policy with
                {
                    EmojiSets = policy.EmojiSets.Where(e => e.Name != setName).Append(new EmojiSetEntry(setName, EmojiSetEntry.Allowed)).ToList()
                };
            });
            return changed;
        }
    }
}
