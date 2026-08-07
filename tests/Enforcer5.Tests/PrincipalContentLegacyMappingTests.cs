using Enforcer5.Storage.PrincipalContent;
using Xunit;

namespace Enforcer5.Tests;

public sealed class PrincipalContentLegacyMappingTests
{
    [Fact]
    public void PlainCustomCommandIsPreservedExactly()
    {
        var command = LegacyPrincipalContentMapper.ParseCustomCommand(-1001, "#Hello",
            "  Mixed CASE\n<b>markup</b>  ", null, null);

        Assert.Equal("#Hello", command.Trigger);
        Assert.Equal("  Mixed CASE\n<b>markup</b>  ", command.Content);
        Assert.Null(command.MediaType);
        Assert.Null(command.FileId);
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
        Assert.Equal("gif", command.MediaType);
        Assert.Equal("gif-file-id", command.FileId);
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
