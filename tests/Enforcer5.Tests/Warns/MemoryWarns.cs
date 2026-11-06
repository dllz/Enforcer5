using System.Collections.Generic;
using System.Threading.Tasks;
using Enforcer5.Data;
using Enforcer5.Models;

namespace Enforcer5.Tests.Warns
{
    /// <summary>An in-memory <see cref="IWarnRepository"/> with the PostgreSQL one's arithmetic, for the flow tests.</summary>
    internal sealed class MemoryWarns : IWarnRepository
    {
        private readonly Dictionary<(long Chat, WarnKind Kind, long User), (long Active, long Lifetime)> _counts = new();
        private long _nextEvent = 100;
        public readonly List<WarnOp> Applied = new();

        public long Count(long chatId, WarnKind kind, long userId) =>
            _counts.TryGetValue((chatId, kind, userId), out var c) ? c.Active : 0;

        public void Seed(long chatId, WarnKind kind, long userId, long active, long? lifetime = null) =>
            _counts[(chatId, kind, userId)] = (active, lifetime ?? active);

        public Task<long> GetCountAsync(long chatId, WarnKind kind, long userId) => Task.FromResult(Count(chatId, kind, userId));

        public Task<WarnCounts> GetCountsAsync(long chatId, long userId)
        {
            _counts.TryGetValue((chatId, WarnKind.Warn, userId), out var warns);
            return Task.FromResult(new WarnCounts(warns.Active, Count(chatId, WarnKind.Prewarn, userId),
                Count(chatId, WarnKind.Media, userId), warns.Lifetime));
        }

        public Task<WarnChanged> ApplyAsync(WarnOp op)
        {
            Applied.Add(op);
            _counts.TryGetValue((op.ChatId, op.Kind, op.UserId), out var c);
            var after = PostgresWarnRepository.After(op.Operation, c.Active, op.Quantity);
            var lifetime = PostgresWarnRepository.LifetimeAfter(op.Operation, c.Lifetime, c.Active, after);
            _counts[(op.ChatId, op.Kind, op.UserId)] = (after, lifetime);
            return Task.FromResult(new WarnChanged(c.Active, after, lifetime, after != c.Active ? _nextEvent++ : null));
        }

        public Task SetLifetimeAsync(long chatId, long userId, long lifetime)
        {
            _counts.TryGetValue((chatId, WarnKind.Warn, userId), out var c);
            _counts[(chatId, WarnKind.Warn, userId)] = (c.Active, lifetime);
            return Task.CompletedTask;
        }
    }
}
