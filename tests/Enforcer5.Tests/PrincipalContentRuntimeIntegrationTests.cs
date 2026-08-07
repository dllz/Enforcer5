using Dapper;
using Enforcer5.Helpers;
using Enforcer5.Storage;
using Enforcer5.Storage.PrincipalContent;
using Npgsql;
using Xunit;

namespace Enforcer5.Tests;

public sealed class PrincipalContentRuntimeIntegrationTests
{
    [PostgresFact]
    public async Task PostgresPrimaryRuntimeUsesCanonicalRepositoriesAndCreatesMirrorOutbox()
    {
        var originalMode = Environment.GetEnvironmentVariable("StorageMode");
        var originalConnection = Environment.GetEnvironmentVariable("PostgresConnection");
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_content_runtime_{Guid.NewGuid():N}";
        var quotedSchema = $"\"{schema}\"";

        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA {quotedSchema}");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
            await SchemaMigrationRunner.ApplyAsync(builder.ConnectionString);
            Environment.SetEnvironmentVariable("StorageMode", nameof(StorageMode.PostgresPrimaryRedisMirror));
            Environment.SetEnvironmentVariable("PostgresConnection", builder.ConnectionString);
            const long chatId = -100987654321;

            await using (var seed = new NpgsqlConnection(builder.ConnectionString))
            {
                await seed.OpenAsync();
                await seed.ExecuteAsync("INSERT INTO groups (chat_id, name) VALUES (@ChatId, 'runtime test')",
                    new { ChatId = chatId });
            }

            Methods.WriteGroupContent(chatId, GroupContentTypes.Rules, "canonical rules");
            Methods.WriteCustomCommand(new CustomCommand(chatId, "#Clip", "caption text", "gif", "file-id", null));

            Assert.Equal("canonical rules", Methods.ReadGroupContent(chatId, GroupContentTypes.Rules));
            Assert.Equal("file-id", Methods.ReadCustomCommand(chatId, "#Clip")!.FileId);
            Assert.Equal("#Clip", Assert.Single(Methods.ListCustomCommands(chatId)).Trigger);

            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM compatibility_outbox WHERE completed_at IS NULL"));

            Assert.True(Methods.DeleteCustomCommand(chatId, "#Clip"));
            Methods.DeleteGroupContent(chatId, GroupContentTypes.Rules);
            Assert.Null(Methods.ReadCustomCommand(chatId, "#Clip"));
            Assert.Null(Methods.ReadGroupContent(chatId, GroupContentTypes.Rules));
            Assert.Equal(4, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM compatibility_outbox WHERE completed_at IS NULL"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("StorageMode", originalMode);
            Environment.SetEnvironmentVariable("PostgresConnection", originalConnection);
            await admin.ExecuteAsync($"DROP SCHEMA {quotedSchema} CASCADE");
        }
    }
}
