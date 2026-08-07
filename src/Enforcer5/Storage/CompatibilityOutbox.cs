using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Newtonsoft.Json;

namespace Enforcer5.Storage
{
    internal static class CompatibilityOutbox
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver()
        };
        internal static Task EnqueueAsync(DbConnection connection, DbTransaction transaction,
            string domain, string orderingKey, string operation, object payload,
            CancellationToken cancellationToken = default)
        {
            return connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO compatibility_outbox (id, domain, ordering_key, operation, payload, created_at, next_attempt_at)
VALUES (@Id, @Domain, @OrderingKey, @Operation, CAST(@Payload AS jsonb), clock_timestamp(), clock_timestamp())",
                new
                {
                    Id = Guid.NewGuid(),
                    Domain = domain,
                    OrderingKey = orderingKey,
                    Operation = operation,
                    Payload = JsonConvert.SerializeObject(payload, JsonSettings)
                }, transaction, cancellationToken: cancellationToken));
        }
    }
}
