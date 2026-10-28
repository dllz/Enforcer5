using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Enforcer5.Storage;

namespace Enforcer5.Data
{
    /// <summary>What a profile lookup found about an account: flags only, never the bio (<c>account_profile_flags</c>).</summary>
    internal sealed record AccountProfileFlags(long UserId, IReadOnlyList<string> Flags, long? PersonalChannelId, string BioScript,
        DateTimeOffset CheckedAt);

    /// <summary>A public channel or group as a lookup found it: ids, username and flags, never its text (<c>channel_profiles</c>).</summary>
    internal sealed record ChannelProfileRow(long ChatId, string ChatType, string Username, IReadOnlyList<string> Flags,
        int? MemberCount, long? LinkedChatId, DateTimeOffset CheckedAt);

    /// <summary>What Detection's profile and channel lookups keep (build step 11).</summary>
    internal interface IProfileRepository
    {
        Task SaveAccountAsync(AccountProfileFlags profile);

        Task SaveChannelAsync(ChannelProfileRow channel);

        Task<AccountProfileFlags> GetAccountAsync(long userId);

        Task<ChannelProfileRow> GetChannelAsync(long chatId);
    }

    internal sealed class PostgresProfileRepository : IProfileRepository
    {
        private readonly Func<PostgresDatabase> _database;

        internal PostgresProfileRepository(Func<PostgresDatabase> database)
        {
            _database = database;
        }

        private PostgresDatabase Database => _database() ?? throw new InvalidOperationException("PostgreSQL is not ready.");

        public async Task SaveAccountAsync(AccountProfileFlags profile)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_xact_lock(@Lock)",
                new { Lock = PostgresPrincipalRepository.PrincipalLock }, transaction));
            var account = await PrincipalResolver.EnsureUserAsync(connection, transaction, profile.UserId);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO account_profile_flags (account_id, flags, personal_channel_id, bio_script, checked_at)
VALUES (@AccountId, @Flags, @Channel, @Script, @At)
ON CONFLICT (account_id) DO UPDATE SET flags = excluded.flags, personal_channel_id = excluded.personal_channel_id,
    bio_script = excluded.bio_script, checked_at = excluded.checked_at",
                new
                {
                    account.AccountId, Flags = profile.Flags.ToArray(), Channel = profile.PersonalChannelId, Script = profile.BioScript,
                    At = profile.CheckedAt.UtcDateTime
                }, transaction));
            await transaction.CommitAsync();
        }

        public async Task SaveChannelAsync(ChannelProfileRow channel)
        {
            await using var connection = await Database.OpenConnectionAsync();
            await connection.ExecuteAsync(@"
INSERT INTO channel_profiles (telegram_chat_id, chat_type, username, flags, member_count, linked_chat_id, checked_at)
VALUES (@ChatId, @ChatType, @Username, @Flags, @MemberCount, @LinkedChatId, @At)
ON CONFLICT (telegram_chat_id) DO UPDATE SET chat_type = excluded.chat_type, username = excluded.username, flags = excluded.flags,
    member_count = COALESCE(excluded.member_count, channel_profiles.member_count), linked_chat_id = excluded.linked_chat_id,
    checked_at = excluded.checked_at",
                new
                {
                    channel.ChatId, channel.ChatType, channel.Username, Flags = channel.Flags.ToArray(), channel.MemberCount,
                    channel.LinkedChatId, At = channel.CheckedAt.UtcDateTime
                });
        }

        private sealed class AccountRow
        {
            public long UserId { get; set; }
            public string[] Flags { get; set; }
            public long? PersonalChannelId { get; set; }
            public string BioScript { get; set; }
            public DateTime CheckedAt { get; set; }
        }

        public async Task<AccountProfileFlags> GetAccountAsync(long userId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var row = await connection.QuerySingleOrDefaultAsync<AccountRow>(@"
SELECT a.telegram_user_id AS UserId, p.flags AS Flags, p.personal_channel_id AS PersonalChannelId, p.bio_script AS BioScript,
       p.checked_at AS CheckedAt
FROM account_profile_flags p JOIN telegram_accounts a ON a.account_id = p.account_id
WHERE a.telegram_user_id = @UserId", new { UserId = userId });
            return row == null
                ? null
                : new AccountProfileFlags(row.UserId, row.Flags, row.PersonalChannelId, row.BioScript,
                    new DateTimeOffset(DateTime.SpecifyKind(row.CheckedAt, DateTimeKind.Utc)));
        }

        private sealed class ChannelRow
        {
            public long ChatId { get; set; }
            public string ChatType { get; set; }
            public string Username { get; set; }
            public string[] Flags { get; set; }
            public int? MemberCount { get; set; }
            public long? LinkedChatId { get; set; }
            public DateTime CheckedAt { get; set; }
        }

        public async Task<ChannelProfileRow> GetChannelAsync(long chatId)
        {
            await using var connection = await Database.OpenConnectionAsync();
            var row = await connection.QuerySingleOrDefaultAsync<ChannelRow>(@"
SELECT telegram_chat_id AS ChatId, chat_type AS ChatType, username AS Username, flags AS Flags, member_count AS MemberCount,
       linked_chat_id AS LinkedChatId, checked_at AS CheckedAt
FROM channel_profiles WHERE telegram_chat_id = @ChatId", new { ChatId = chatId });
            return row == null
                ? null
                : new ChannelProfileRow(row.ChatId, row.ChatType, row.Username, row.Flags, row.MemberCount, row.LinkedChatId,
                    new DateTimeOffset(DateTime.SpecifyKind(row.CheckedAt, DateTimeKind.Utc)));
        }
    }
}
