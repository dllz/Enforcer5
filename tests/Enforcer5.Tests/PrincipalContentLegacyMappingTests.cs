using Enforcer5.Storage.PrincipalContent;
using Xunit;

namespace Enforcer5.Tests;

public sealed class PrincipalContentLegacyMappingTests
{
    [Fact]
    public void PlainCustomCommandNormalizesTriggerAndPreservesContentExactly()
    {
        var command = LegacyPrincipalContentMapper.ParseCustomCommand(-1001, "#Hello",
            "  Mixed CASE\n<b>markup</b>  ", null, null);

        Assert.Equal("hello", command.Trigger);
        Assert.Equal("  Mixed CASE\n<b>markup</b>  ", command.Content);
        Assert.Null(command.MediaType);
        Assert.Null(command.FileId);
    }

    [Theory]
    [InlineData("#deadtalk❗️", "deadtalk")]
    [InlineData("#deadtalk❗️Deadtalk", "deadtalk")]
    [InlineData("#Hello_world42", "hello_world42")]
    [InlineData("#سلام_دنیا", "سلام_دنیا")]
    [InlineData("#東京テスト", "東京テスト")]
    [InlineData("#CAFÉ", "café")]
    [InlineData("#CAFÉ", "café")]
    public void CustomCommandTriggerDropsPrefixAndTrailingNonHashtagText(string trigger, string expected)
    {
        Assert.Equal(expected, LegacyPrincipalContentMapper.NormalizeCustomCommandTrigger(trigger));
    }

    [Fact]
    public void EncodedMediaCommandParsesTypeFileAndSideCaption()
    {
        var command = LegacyPrincipalContentMapper.ParseCustomCommand(-1001, "#clip",
            "###file_id!video###:AA:BB==", null, "caption\nexact");

        Assert.Null(command.Content);
        Assert.Equal("video", command.MediaType);
        Assert.Equal("AA:BB==", command.FileId);
        Assert.Equal("caption\nexact", command.Caption);
    }

    [Fact]
    public void GifSideHashRetainsMainText()
    {
        var command = LegacyPrincipalContentMapper.ParseCustomCommand(-1001, "#gif",
            "text caption", "gif-file-id", null);

        Assert.Equal("text caption", command.Content);
        Assert.Equal("animation", command.MediaType);
        Assert.Equal("gif-file-id", command.FileId);
    }

    [Theory]
    [InlineData("gif", "animation")]
    [InlineData("file", "document")]
    [InlineData("text", "document")]
    [InlineData("unknown", "document")]
    [InlineData("videoNote", "video_note")]
    [InlineData("voice", "voice")]
    public void LegacyMediaTypesMapToCanonicalTypes(string mediaType, string expected)
    {
        Assert.Equal(expected, LegacyPrincipalContentMapper.NormalizeCustomCommandMediaType(mediaType));
    }

    [Fact]
    public void MalformedMediaMarkerIsRetainedAsText()
    {
        var raw = "###file_id!video-without-separator";
        var command = LegacyPrincipalContentMapper.ParseCustomCommand(-1001, "#broken", raw, null, null);

        Assert.Equal(raw, command.Content);
        Assert.Null(command.MediaType);
    }

    [Fact]
    public void ContentMappingUsesExactKindsValuesAndOrder()
    {
        var items = LegacyPrincipalContentMapper.MapContent(-1001,
            " rules \n", "<b>About</b>", "NL-nl", "https://t.me/CaseSensitive");

        Assert.Collection(items,
            item => Assert.Equal(new GroupContentItem(-1001, "rules", " rules \n"), item),
            item => Assert.Equal(new GroupContentItem(-1001, "about", "<b>About</b>"), item),
            item => Assert.Equal(new GroupContentItem(-1001, "language", "NL-nl"), item),
            item => Assert.Equal(new GroupContentItem(-1001, "link", "https://t.me/CaseSensitive"), item));
    }

    [Fact]
    public void ContentMappingOmitsMissingButPreservesEmptyValues()
    {
        var items = LegacyPrincipalContentMapper.MapContent(-1001, null, "", null, "no");

        Assert.Collection(items,
            item => Assert.Equal(new GroupContentItem(-1001, "about", ""), item),
            item => Assert.Equal(new GroupContentItem(-1001, "link", "no"), item));
    }
}
