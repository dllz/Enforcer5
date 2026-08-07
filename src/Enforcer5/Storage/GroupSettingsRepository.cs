using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;

namespace Enforcer5.Storage
{
    internal sealed class GroupSettingsRepository
    {
        private readonly PostgresDatabase _database;

        internal GroupSettingsRepository(PostgresDatabase database)
        {
            _database = database;
        }

        internal async Task<Dictionary<string, string>> GetAsync(long chatId, string settingGroup,
            CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            var json = await connection.ExecuteScalarAsync<string>(new CommandDefinition(@"
SELECT values::text FROM group_settings
WHERE chat_id = @ChatId AND setting_group = @SettingGroup",
                new { ChatId = chatId, SettingGroup = settingGroup }, cancellationToken: cancellationToken));
            return json == null
                ? new Dictionary<string, string>()
                : JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                  ?? new Dictionary<string, string>();
        }

        internal async Task<long> MutateCanonicalAsync(long chatId, string settingGroup, string operation,
            string field, string value, bool mirrorRedis, CancellationToken cancellationToken = default)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                "SELECT pg_advisory_xact_lock(@ChatId, hashtext(@SettingGroup))",
                new { ChatId = chatId, SettingGroup = settingGroup }, transaction,
                cancellationToken: cancellationToken));
            var currentJson = await connection.ExecuteScalarAsync<string>(new CommandDefinition(@"
SELECT values::text FROM group_settings
WHERE chat_id = @ChatId AND setting_group = @SettingGroup
FOR UPDATE", new { ChatId = chatId, SettingGroup = settingGroup }, transaction,
                cancellationToken: cancellationToken));
            var fields = currentJson == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : JsonConvert.DeserializeObject<Dictionary<string, string>>(currentJson)
                  ?? new Dictionary<string, string>(StringComparer.Ordinal);

            long result;
            if (operation == "delete")
            {
                result = fields.Remove(field) ? 1 : 0;
            }
            else if (operation == "increment")
            {
                fields.TryGetValue(field, out var existing);
                long.TryParse(existing, out var number);
                result = checked(number + long.Parse(value));
                fields[field] = result.ToString();
            }
            else
            {
                var added = !fields.ContainsKey(field);
                fields[field] = value;
                result = added ? 1 : 0;
            }

            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO group_settings (chat_id, setting_group, values)
VALUES (@ChatId, @SettingGroup, CAST(@Values AS jsonb))
ON CONFLICT (chat_id, setting_group) DO UPDATE
SET values = EXCLUDED.values, version = group_settings.version + 1, updated_at = now()",
                new { ChatId = chatId, SettingGroup = settingGroup, Values = JsonConvert.SerializeObject(fields) },
                transaction, cancellationToken: cancellationToken));

            if (mirrorRedis)
            {
                await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO compatibility_outbox (id, domain, ordering_key, operation, payload, created_at, next_attempt_at)
VALUES (@Id, 'group-settings', @OrderingKey, @Operation, CAST(@Payload AS jsonb), clock_timestamp(), clock_timestamp())",
                    new
                    {
                        Id = Guid.NewGuid(),
                        OrderingKey = $"group-settings:{chatId}:{settingGroup}",
                        Operation = operation,
                        Payload = JsonConvert.SerializeObject(new
                        {
                            chatId,
                            settingGroup,
                            field,
                            value,
                            resultValue = fields.TryGetValue(field, out var resultingValue)
                                ? resultingValue
                                : null
                        })
                    }, transaction, cancellationToken: cancellationToken));
            }

            await transaction.CommitAsync(cancellationToken);
            return result;
        }

        internal async Task<bool> UpsertAsync(GroupSettingsSnapshot snapshot,
            CancellationToken cancellationToken = default, bool force = false,
            long? expectedCurrentRevision = null)
        {
            await using var connection = await _database.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var accepted = await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO group_settings_redis_sync (
    chat_id, setting_group, redis_key, source_exists, source_revision, source_checksum, observed_at)
VALUES (
    @ChatId, @SettingGroup, @RedisKey, @SourceExists, @SourceRevision, @SourceChecksum, @ObservedAt)
ON CONFLICT (chat_id, setting_group) DO UPDATE
SET redis_key = EXCLUDED.redis_key,
    source_exists = EXCLUDED.source_exists,
    source_revision = EXCLUDED.source_revision,
    source_checksum = EXCLUDED.source_checksum,
    observed_at = EXCLUDED.observed_at,
    persisted_at = now()
WHERE EXCLUDED.source_revision > group_settings_redis_sync.source_revision
   OR (@Force AND group_settings_redis_sync.source_revision = @ExpectedCurrentRevision)",
                new
                {
                    snapshot.ChatId,
                    snapshot.SettingGroup,
                    snapshot.RedisKey,
                    snapshot.SourceExists,
                    snapshot.SourceRevision,
                    SourceChecksum = snapshot.Checksum,
                    snapshot.ObservedAt,
                    Force = force,
                    ExpectedCurrentRevision = expectedCurrentRevision
                }, transaction, cancellationToken: cancellationToken));

            if (accepted == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            if (snapshot.SourceExists)
            {
                await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO group_settings (chat_id, setting_group, values)
VALUES (@ChatId, @SettingGroup, CAST(@Values AS jsonb))
ON CONFLICT (chat_id, setting_group) DO UPDATE
SET values = EXCLUDED.values,
    version = group_settings.version + 1,
    updated_at = now()",
                    new
                    {
                        snapshot.ChatId,
                        snapshot.SettingGroup,
                        Values = JsonConvert.SerializeObject(snapshot.Fields)
                    }, transaction, cancellationToken: cancellationToken));
            }
            else
            {
                await connection.ExecuteAsync(new CommandDefinition(@"
DELETE FROM group_settings WHERE chat_id = @ChatId AND setting_group = @SettingGroup",
                    new { snapshot.ChatId, snapshot.SettingGroup }, transaction,
                    cancellationToken: cancellationToken));
            }

            await transaction.CommitAsync(cancellationToken);
            return true;
        }
    }
}
