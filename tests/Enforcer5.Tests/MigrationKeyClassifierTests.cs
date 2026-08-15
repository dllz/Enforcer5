using Enforcer5.Storage;
using Enforcer5.Storage.Loading;
using Xunit;

namespace Enforcer5.Tests;

/// <summary>
/// The inventory classifier decides which group owns a key, and a wrong answer silently migrates
/// one group's data into another. It runs on 19.4M keys with no round trips, so shape is the only
/// signal available - these pin the irregular shapes that a general rule gets wrong.
/// </summary>
public sealed class MigrationKeyClassifierTests
{
    [Theory]
    [InlineData("user:42", MigrationKeyClasses.User, null, 42L)]
    [InlineData("globalBan:42", MigrationKeyClasses.GlobalBan, null, 42L)]
    [InlineData("ban:42", MigrationKeyClasses.ModerationCounters, null, 42L)]
    [InlineData("-1001234567890:users:42", MigrationKeyClasses.GroupStatistics, -1001234567890L, 42L)]
    [InlineData("flagged:-1001234567890:777", MigrationKeyClasses.Report, -1001234567890L, null)]
    [InlineData("flaggedReply:-1001234567890:777", MigrationKeyClasses.Report, -1001234567890L, null)]
    [InlineData("chat:-1001234567890:settings", MigrationKeyClasses.GroupScoped, -1001234567890L, null)]
    public void RegularShapesCarryTheirOwner(string key, string expectedClass,
        long? expectedChatId, long? expectedUserId)
    {
        var classified = MigrationKeyClassifier.Classify(key);

        Assert.Equal(expectedClass, classified.KeyClass);
        Assert.Equal(expectedChatId, classified.TelegramChatId);
        Assert.Equal(expectedUserId, classified.TelegramUserId);
    }

    [Fact]
    public void OnlyTheSignSeparatesAUsersLifetimeTotalFromAGroup()
    {
        // "chat:{userId}" is the user's global lifetime message total, even though every other
        // "chat:" key is group-scoped. Getting this wrong would attribute 4.75M user counters to
        // nonexistent groups - or drop them.
        var user = MigrationKeyClassifier.Classify("chat:42");
        Assert.Equal(MigrationKeyClasses.UserMessageTotal, user.KeyClass);
        Assert.Equal(42L, user.TelegramUserId);
        Assert.Null(user.TelegramChatId);

        var group = MigrationKeyClassifier.Classify("chat:-1001234567890");
        Assert.Equal(MigrationKeyClasses.GroupScoped, group.KeyClass);
        Assert.Equal(-1001234567890L, group.TelegramChatId);
        Assert.Null(group.TelegramUserId);
    }

    [Fact]
    public void TheColonlessLinksSuffixStillResolvesItsGroup()
    {
        // "chat:{id}links" - written with no separator before the suffix, so a naive
        // split-on-colon reads the id as "-1001234567890links" and drops the key.
        var classified = MigrationKeyClassifier.Classify("chat:-1001234567890links");

        Assert.Equal(MigrationKeyClasses.GroupScoped, classified.KeyClass);
        Assert.Equal(-1001234567890L, classified.TelegramChatId);
    }

    [Fact]
    public void BothUserJoinShapesResolveToTheSameGroup()
    {
        // Written as "chat:{id}:userJoin" from two call sites and bare "{id}:userJoin" from two
        // others. Both are the same data and must land on the same owner.
        var prefixed = MigrationKeyClassifier.Classify("chat:-1001234567890:userJoin");
        var bare = MigrationKeyClassifier.Classify("-1001234567890:userJoin");

        Assert.Equal(-1001234567890L, prefixed.TelegramChatId);
        Assert.Equal(-1001234567890L, bare.TelegramChatId);
        Assert.Equal(MigrationKeyClasses.GroupScoped, bare.KeyClass);
    }

    [Fact]
    public void APerUserGroupKeyCarriesBothOwners()
    {
        var classified = MigrationKeyClassifier.Classify("chat:-1001234567890:tempbanned:42");

        Assert.Equal(MigrationKeyClasses.GroupScoped, classified.KeyClass);
        Assert.Equal(-1001234567890L, classified.TelegramChatId);
        Assert.Equal(42L, classified.TelegramUserId);
    }

    [Theory]
    [InlineData("bot:general")]
    [InlineData("bot:groupsid")]
    [InlineData("tempbanned")]
    [InlineData("tempbannedPremium")]
    [InlineData("logChatGroups")]
    public void BotWideKeysHaveNoGroupDimension(string key)
    {
        // The two tempban hashes are the reason blocker B1 existed: they hold group-scoped facts
        // in a bot-wide structure, so they must never be attributed to a group.
        var classified = MigrationKeyClassifier.Classify(key);

        Assert.Equal(MigrationKeyClasses.BotGlobal, classified.KeyClass);
        Assert.Null(classified.TelegramChatId);
    }

    [Theory]
    [InlineData("user:42:query", null)]
    [InlineData("spam:-1001234567890:42", null)]
    [InlineData("spam:added:42", null)]
    [InlineData("chat:-1001234567890:adminses:42", -1001234567890L)]
    [InlineData("chat:-1001234567890:blockList", -1001234567890L)]
    public void EphemeralKeysAreRecognisedButStillReportTheirOwnerWhenTheyHaveOne(
        string key, long? expectedChatId)
    {
        // Recording the owner even for keys that are not carried keeps the per-group inventory
        // complete, which is what lets a cutover verify it has accounted for everything it saw.
        var classified = MigrationKeyClassifier.Classify(key);

        Assert.Equal(MigrationKeyClasses.Ephemeral, classified.KeyClass);
        Assert.Equal(expectedChatId, classified.TelegramChatId);
    }

    [Fact]
    public void SupersededShapesAreRecognisedRatherThanLeftUnknown()
    {
        // lang:{id} predates chat:{chatId}:language and still holds 1,325 live keys in the
        // production copy. Nothing reads it, but leaving it unclassified would hide it in the
        // bucket that is supposed to mean "investigate this".
        var group = MigrationKeyClassifier.Classify("lang:-1001234567890");
        Assert.Equal(MigrationKeyClasses.Legacy, group.KeyClass);
        Assert.Equal(-1001234567890L, group.TelegramChatId);

        var user = MigrationKeyClassifier.Classify("lang:42");
        Assert.Equal(MigrationKeyClasses.Legacy, user.KeyClass);
        Assert.Equal(42L, user.TelegramUserId);
    }

    [Fact]
    public void AGroupLevelBanIsAttributedToItsGroup()
    {
        var classified = MigrationKeyClassifier.Classify("groupBan:-1001234567890");

        Assert.Equal(MigrationKeyClasses.GroupScoped, classified.KeyClass);
        Assert.Equal(-1001234567890L, classified.TelegramChatId);
    }

    [Theory]
    [InlineData("")]
    [InlineData("something:entirely:new")]
    [InlineData("user:notanumber")]
    [InlineData("chat:notanumber:settings")]
    [InlineData("antinamelengthsettings")]
    public void AnUnrecognisedShapeIsReportedRatherThanGuessedAt(string key)
    {
        // A new key shape must surface as a rising unclassified count to be investigated, never as
        // a plausible-looking wrong owner. The bare setting-group name is a real orphan found in
        // production - a settings hash written with no chat prefix - and belongs in this bucket.
        Assert.True(MigrationKeyClassifier.Classify(key).IsUnclassified);
    }
}
