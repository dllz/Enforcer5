using Enforcer5.Helpers;
using Enforcer5.Storage;
using Enforcer5.Storage.Loading;
using Enforcer5.Storage.Operations;
using StackExchange.Redis;
using Xunit;

namespace Enforcer5.Tests;

/// <summary>
/// Tempbans live in two places that have diverged in both directions.
/// <para>
/// Measured on the production copy: 102 (group, user) pairs in the process-wide hashes, 89 as
/// per-group keys, and only 56 in both. Neither is a superset, so reading either alone loses
/// tempbans - and reading the global hash alone, which is what the migration did, was silently
/// dropping the 33 that exist only per group.
/// </para>
/// <para>
/// The divergence is structural. The global hash is keyed by expiry <em>second</em> across every
/// group, so two bans expiring in the same second collide and one is overwritten - the same defect
/// that produced the infinite collision loop.
/// </para>
/// </summary>
[Collection("GroupDrainBuffer")]
public sealed class TempBanIndexTests
{
    [RedisFact]
    public async Task TheMigrationReadsBothSourcesBecauseNeitherIsASuperset()
    {
        var groupId = -1008800000000L - Random.Shared.NextInt64(1, 1_000_000);
        var inBoth = 811_000_000L + Random.Shared.NextInt64(1, 1_000_000);
        var globalOnly = 822_000_000L + Random.Shared.NextInt64(1, 1_000_000);
        var perGroupOnly = 833_000_000L + Random.Shared.NextInt64(1, 1_000_000);
        var lapsed = 844_000_000L + Random.Shared.NextInt64(1, 1_000_000);

        await InRedisAsync(groupId, async () =>
        {
            var future = DateTimeOffset.UtcNow.AddDays(7).ToUnixTimeSeconds();

            // In both: the per-group value is the global field verbatim, which is the link
            // RemoveTempBan follows to delete the global row.
            await Redis.db.HashSetAsync("tempbanned", future, $"{groupId}:{inBoth}:Nick:Group");
            await Redis.db.StringSetAsync($"chat:{groupId}:tempbanned:{inBoth}", future,
                TimeSpan.FromDays(7));
            await Redis.db.SetAddAsync($"chat:{groupId}:tempbanned", inBoth);

            // Global only - the per-group key was lost.
            await Redis.db.HashSetAsync("tempbanned", future + 1, $"{groupId}:{globalOnly}:Nick:Group");

            // Per-group only - the global entry was lost, most plausibly to a slot collision.
            await Redis.db.StringSetAsync($"chat:{groupId}:tempbanned:{perGroupOnly}", future + 2,
                TimeSpan.FromDays(7));
            await Redis.db.SetAddAsync($"chat:{groupId}:tempbanned", perGroupOnly);

            // A stale set member whose expiry key has already lapsed. The member set has no TTL, so
            // it accumulates these; the absent string is what says the ban is over.
            await Redis.db.SetAddAsync($"chat:{groupId}:tempbanned", lapsed);

            var snapshot = await new LegacyOperationsReader().ReadAsync(
                new[] { groupId }, Array.Empty<long>(),
                new Dictionary<long, IReadOnlyCollection<long>>(), default, includeGlobalData: false);

            var tempbans = snapshot.Sanctions
                .Where(x => x.Kind == SanctionKinds.TempBan && x.GroupId == groupId)
                .ToList();

            Assert.Contains(tempbans, x => x.UserId == inBoth);
            Assert.Contains(tempbans, x => x.UserId == globalOnly);
            // The one the migration used to drop.
            Assert.Contains(tempbans, x => x.UserId == perGroupOnly);
            // A lapsed ban must not be resurrected.
            Assert.DoesNotContain(tempbans, x => x.UserId == lapsed);
            // Present in both sources, emitted once - a duplicate would be two sanction rows for
            // one ban, with different legacy keys.
            Assert.Single(tempbans, x => x.UserId == inBoth);
        });
    }

    [RedisFact]
    public async Task AnImplausibleExpiryIsRecordedButCarriedUnchanged()
    {
        // 128 of 141 entries on the production copy expire more than ten years out, some in 2969.
        // The decision is to keep them: an expiry that has not arrived describes a ban still in
        // force, and clamping it would silently shorten a real sanction. It is recorded so the
        // anomaly is visible rather than quietly accepted.
        var groupId = -1008700000000L - Random.Shared.NextInt64(1, 1_000_000);
        var userId = 866_000_000L + Random.Shared.NextInt64(1, 1_000_000);

        await InRedisAsync(groupId, async () =>
        {
            var year2969 = 31551037961L;
            await Redis.db.HashSetAsync("tempbanned", year2969, $"{groupId}:{userId}:Nick:Group");

            var snapshot = await new LegacyOperationsReader().ReadAsync(
                new[] { groupId }, Array.Empty<long>(),
                new Dictionary<long, IReadOnlyCollection<long>>(), default, includeGlobalData: false);

            var tempban = Assert.Single(snapshot.Sanctions,
                x => x.Kind == SanctionKinds.TempBan && x.UserId == userId);
            // Carried unchanged - not clamped, not dropped.
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(year2969), tempban.ExpiresAt);

            // Recorded as a data-quality issue, not an error: it was read and migrated, its source
            // is simply questionable. Errors are what fail a run.
            Assert.Contains(snapshot.DataQualityIssues, x => x.Message.Contains("plausible ban length"));
            Assert.Empty(snapshot.Errors);
        });
    }

    [RedisFact]
    public async Task MaterialisingGivesGlobalOnlyEntriesAPerGroupHomeAndIsIdempotent()
    {
        var groupId = -1008900000000L - Random.Shared.NextInt64(1, 1_000_000);
        var userId = 855_000_000L + Random.Shared.NextInt64(1, 1_000_000);

        await InRedisAsync(groupId, async () =>
        {
            var future = DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds();
            await Redis.db.HashSetAsync("tempbanned", future, $"{groupId}:{userId}:Nick:Group");

            var dryRun = await TempBanIndexMaterialiser.RunAsync(apply: false);
            Assert.True(dryRun.Materialised >= 1);
            // A dry run must not write.
            Assert.False(await Redis.db.KeyExistsAsync($"chat:{groupId}:tempbanned:{userId}"));

            await TempBanIndexMaterialiser.RunAsync(apply: true);

            // The value is the global field verbatim, or the next unban would orphan the global row.
            Assert.Equal(future.ToString(),
                (await Redis.db.StringGetAsync($"chat:{groupId}:tempbanned:{userId}")).ToString());
            Assert.True(await Redis.db.SetContainsAsync($"chat:{groupId}:tempbanned", userId));

            var second = await TempBanIndexMaterialiser.RunAsync(apply: true);
            // Additive and idempotent: an existing key is never rewritten, so a re-run cannot give a
            // live tempban a fresh expiry.
            Assert.Equal(0, second.Materialised);
        });
    }

    private static async Task InRedisAsync(long groupId, Func<Task> body)
    {
        using var config = GlobalConfigScope.Acquire("RedisConnection", "StorageMode",
            "PostgresConnection", "OperationsShadowEnabled");
        config.Set("RedisConnection",
            Environment.GetEnvironmentVariable(RedisFactAttribute.ConnectionVariable));
        config.Set("StorageMode", nameof(StorageMode.RedisPrimaryPostgresShadow));
        config.Set("OperationsShadowEnabled", "false");
        Assert.True(Redis.Start());

        var keys = new List<RedisKey>
        {
            $"chat:{groupId}:tempbanned", $"chat:{groupId}:tempbannedPremium",
            $"chat:{groupId}:userlast", $"chat:{groupId}:bannedlist", $"chat:{groupId}:tempmuted"
        };
        try
        {
            await body();
        }
        finally
        {
            await foreach (var key in ScanAsync($"chat:{groupId}:tempbanned:*")) keys.Add(key);
            await Redis.db.KeyDeleteAsync(keys.ToArray());
            // Leave the shared global hashes as they were found.
            foreach (var hash in new[] { "tempbanned", "tempbannedPremium" })
                foreach (var entry in await Redis.db.HashGetAllAsync(hash))
                    if (entry.Value.ToString().StartsWith($"{groupId}:", StringComparison.Ordinal))
                        await Redis.db.HashDeleteAsync(hash, entry.Name);
        }
    }

    private static async IAsyncEnumerable<RedisKey> ScanAsync(string pattern)
    {
        var cursor = "0";
        do
        {
            var page = (RedisResult[])await Redis.db.ExecuteAsync("SCAN", cursor, "MATCH", pattern,
                "COUNT", 1000);
            cursor = page[0].ToString();
            foreach (var key in (RedisResult[])page[1]) yield return key.ToString();
        } while (cursor != "0");
    }
}
