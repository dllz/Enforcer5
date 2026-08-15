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
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_content_runtime_{Guid.NewGuid():N}";
        var quotedSchema = $"\"{schema}\"";

        // This test repoints StorageMode and PostgresConnection at a throwaway schema. Both are
        // read live by static entry points elsewhere, so it must not overlap tests observing them.
        using var config = GlobalConfigScope.Acquire("StorageMode", "PostgresConnection");

        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA {quotedSchema}");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
            await SchemaMigrationRunner.ApplyAsync(builder.ConnectionString);
            config.Set("StorageMode", nameof(StorageMode.PostgresPrimaryRedisMirror));
            config.Set("PostgresConnection", builder.ConnectionString);
            const long chatId = -100987654321;

            await using (var database = new PostgresDatabase(builder.ConnectionString))
                await new GroupRepository(database).UpsertAsync(new GroupPrincipal(chatId, "runtime test"));

            Methods.WriteGroupContent(chatId, GroupContentTypes.Rules, "canonical rules");
            Methods.WriteCustomCommand(new CustomCommand(chatId, "#Clip", "caption text", "gif", "file-id", null));

            Assert.Equal("canonical rules", Methods.ReadGroupContent(chatId, GroupContentTypes.Rules));
            Assert.Equal("file-id", Methods.ReadCustomCommand(chatId, "#Clip")!.FileId);
            Assert.Equal("clip", Assert.Single(Methods.ListCustomCommands(chatId)).Trigger);

            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync();
            Assert.Equal(3, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM compatibility_outbox"));

            Assert.True(Methods.DeleteCustomCommand(chatId, "#Clip"));
            Methods.DeleteGroupContent(chatId, GroupContentTypes.Rules);
            Assert.Null(Methods.ReadCustomCommand(chatId, "#Clip"));
            Assert.Null(Methods.ReadGroupContent(chatId, GroupContentTypes.Rules));
            Assert.Equal(5, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM compatibility_outbox"));
        }
        finally
        {
            await admin.ExecuteAsync($"DROP SCHEMA {quotedSchema} CASCADE");
        }
    }

    [PostgresFact]
    public async Task ConflictResolutionRetainsCandidatesAndCanBeUndone()
    {
        await InSchemaAsync(async database =>
        {
            const long chatId = -100987654322;
            await new GroupRepository(database).UpsertAsync(new GroupPrincipal(chatId, "conflict test"));
            var first = new CustomCommandConflictCandidate(Guid.NewGuid(), chatId, "deadtalk",
                "#DeadTalk❗️", "first", null, null, null);
            var second = new CustomCommandConflictCandidate(Guid.NewGuid(), chatId, "deadtalk",
                "#deadtalk❗️Deadtalk", "second", null, null, null);
            var repository = new CustomCommandConflictRepository(database);
            await repository.ReplaceAsync(chatId, "#DEADTALK", new[] { first, second });

            Assert.Equal(2, (await repository.GetAsync(chatId, "#DeAdTaLk")).Count);
            Assert.Equal("second", (await repository.ResolveAsync(chatId, second.CandidateId))!.Content);
            Assert.Empty(await repository.GetAsync(chatId, "deadtalk"));
            Assert.Equal("second", (await new CustomCommandRepository(database)
                .GetAsync(chatId, "#DEADTALK"))!.Content);

            Assert.True(await repository.UndoAsync(chatId, first.CandidateId));
            Assert.Null(await new CustomCommandRepository(database).GetAsync(chatId, "deadtalk"));
            Assert.Equal(2, (await repository.GetAsync(chatId, "DEADTALK")).Count);

            Assert.True(await repository.DeleteAllAsync(chatId, first.CandidateId));
            Assert.Empty(await repository.GetAsync(chatId, "deadtalk"));
            Assert.True(await repository.UndoAsync(chatId, second.CandidateId));
            Assert.Equal(2, (await repository.GetAsync(chatId, "deadtalk")).Count);

            await using var connection = await database.OpenConnectionAsync();
            Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
                "SELECT count(*) FROM custom_command_conflict_candidates"));
            Assert.Equal(2, await connection.ExecuteScalarAsync<int>(
                "SELECT count(DISTINCT source_trigger) FROM custom_command_conflict_candidates"));
        });
    }

    private static async Task InSchemaAsync(Func<PostgresDatabase, Task> test)
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionVariable)!;
        var schema = $"enforcer_content_conflict_{Guid.NewGuid():N}";
        var quotedSchema = $"\"{schema}\"";
        await using var admin = new NpgsqlConnection(baseConnectionString);
        await admin.OpenAsync();
        await admin.ExecuteAsync($"CREATE SCHEMA {quotedSchema}");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(baseConnectionString) { SearchPath = schema };
            await SchemaMigrationRunner.ApplyAsync(builder.ConnectionString);
            await using var database = new PostgresDatabase(builder.ConnectionString);
            await test(database);
        }
        finally
        {
            await admin.ExecuteAsync($"DROP SCHEMA {quotedSchema} CASCADE");
        }
    }
}
