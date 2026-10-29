using System;
using System.Linq;
using System.Threading.Tasks;
using Enforcer5.Models;
using StackExchange.Redis;

namespace Enforcer5.Data
{
    /// <summary>
    /// The stored values a recovery item reads and sets (<see cref="RecoveryChange"/>): hash fields, set members
    /// ("true" or "false") and string keys in the bot's Redis, and the inline bot and sticker pack blocklists, which
    /// moved to the detection settings in PostgreSQL (types <c>inline</c> and <c>stickerset</c>, "true" or "false",
    /// through their repositories). Only the keys <see cref="Recovery.RecoveryFile"/> accepts for the offer's own group
    /// ever get here.
    /// </summary>
    internal interface IRecoveryValues
    {
        /// <summary>The value now: null when there is none; "true" or "false" for a set member.</summary>
        Task<string> ReadAsync(RecoveryChange change);

        /// <summary>Sets the value; null deletes it, "false" takes a set member out.</summary>
        Task WriteAsync(RecoveryChange change, string value);
    }

    internal sealed class RedisRecoveryValues : IRecoveryValues
    {
        private readonly Func<IDatabaseAsync> _db;

        internal RedisRecoveryValues(Func<IDatabaseAsync> db)
        {
            _db = db;
        }

        /// <summary>The group a blocklist change is for: its key is <c>chat:{id}:...</c>.</summary>
        private static long ChatOf(RecoveryChange change) => long.Parse(change.Key.Split(':')[1]);

        private static InlineBotBlock Block(RecoveryChange change) =>
            InlineBotBlock.TryParse(change.Field, out var block, out _) == InlineBotBlockParseResult.Ok
                ? block
                : throw new ArgumentException($"Not an inline bot block: {change.Field}");

        public async Task<string> ReadAsync(RecoveryChange change)
        {
            var db = _db();
            switch (change.Type)
            {
                case "inline":
                    var block = Block(change);
                    return (await Repositories.InlineBotBlocks.GetAsync(ChatOf(change))).Contains(block) ? "true" : "false";
                case "stickerset":
                    return await Repositories.StickerSetBlocks.ContainsAsync(ChatOf(change), change.Field) ? "true" : "false";
                case "hash":
                    var field = await db.HashGetAsync(change.Key, change.Field);
                    return field.IsNull ? null : field.ToString();
                case "set":
                    return await db.SetContainsAsync(change.Key, change.Field) ? "true" : "false";
                case "str":
                    var value = await db.StringGetAsync(change.Key);
                    return value.IsNull ? null : value.ToString();
                default:
                    throw new ArgumentException($"Unknown recovery change type {change.Type}");
            }
        }

        public async Task WriteAsync(RecoveryChange change, string value)
        {
            var db = _db();
            switch (change.Type)
            {
                case "inline":
                    if (value == "true") await Repositories.InlineBotBlocks.AddAsync(ChatOf(change), Block(change));
                    else await Repositories.InlineBotBlocks.RemoveAsync(ChatOf(change), Block(change));
                    break;
                case "stickerset":
                    if (value == "true") await Repositories.StickerSetBlocks.AddAsync(ChatOf(change), change.Field);
                    else await Repositories.StickerSetBlocks.RemoveAsync(ChatOf(change), change.Field);
                    break;
                case "hash":
                    if (value == null) await db.HashDeleteAsync(change.Key, change.Field);
                    else await db.HashSetAsync(change.Key, change.Field, value);
                    break;
                case "set":
                    if (value == "true") await db.SetAddAsync(change.Key, change.Field);
                    else await db.SetRemoveAsync(change.Key, change.Field);
                    break;
                case "str":
                    if (value == null) await db.KeyDeleteAsync(change.Key);
                    else await db.StringSetAsync(change.Key, value);
                    break;
                default:
                    throw new ArgumentException($"Unknown recovery change type {change.Type}");
            }
        }
    }
}
