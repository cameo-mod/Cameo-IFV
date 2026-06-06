using CameoIFV.Core.Github;
using CameoIFV.Core.Model;
using Xunit;

namespace CameoIFV.Core.Tests;

public class ResolvedReleaseTests
{
    [Fact]
    public void TimelineLabel_IncludesChannelDateAndRelativeAge()
    {
        var release = Release(DateTimeOffset.UtcNow.AddHours(-3));

        Assert.StartsWith($"Dev • {release.PublishedAt.LocalDateTime:yyyy-MM-dd} (", release.TimelineLabel);
        Assert.Contains("hours ago", release.TimelineLabel);
    }

    [Fact]
    public void TimelineLabel_OmitsRelativeAgeAfterSevenDays()
    {
        var release = Release(DateTimeOffset.UtcNow.AddDays(-8));

        Assert.Equal($"Dev • {release.PublishedAt.LocalDateTime:yyyy-MM-dd}", release.TimelineLabel);
    }

    private static ResolvedRelease Release(DateTimeOffset publishedAt)
        => new()
        {
            Channel = ReleaseChannel.Dev,
            TagName = "1.09-DevTest-11",
            DisplayName = "1.09-DevTest-11",
            PublishedAt = publishedAt,
            Prerelease = true,
            AssetUrl = new Uri("https://example/asset.zip"),
            AssetName = "asset.zip",
            AssetSize = 100,
            AssetId = 1,
            AssetUpdatedAt = DateTimeOffset.UtcNow,
        };
}
