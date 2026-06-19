using CameoIFV.Core.Update;
using Xunit;

namespace CameoIFV.Core.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("v2.0.0", 2, 0, 0)]
    [InlineData("2.0.0", 2, 0, 0)]
    [InlineData("1.0.0.0", 1, 0, 0)]          // numeric assembly version
    [InlineData("v2.0.0+abc1234", 2, 0, 0)]   // SourceLink build suffix
    [InlineData("v2.1.0-rc1", 2, 1, 0)]       // pre-release suffix
    [InlineData("Cameo-IFV v1.10.3", 1, 10, 3)]
    public void Parse_ExtractsMajorMinorPatch(string text, int major, int minor, int patch)
    {
        var version = AppVersion.Parse(text);

        Assert.NotNull(version);
        Assert.Equal(new Version(major, minor, patch), version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("v2")]      // incomplete; needs all three components
    [InlineData("2.0")]
    public void Parse_ReturnsNull_WhenNoSemVerCorePresent(string? text)
    {
        Assert.Null(AppVersion.Parse(text));
    }

    [Theory]
    [InlineData("v1.10.0", "v1.2.0")]   // numeric, not lexical, ordering
    [InlineData("v2.0.0", "v1.10.0")]
    [InlineData("v2.0.1", "v2.0.0")]
    public void Parse_OrdersNumerically(string newer, string older)
    {
        Assert.True(AppVersion.Parse(newer) > AppVersion.Parse(older));
    }
}
