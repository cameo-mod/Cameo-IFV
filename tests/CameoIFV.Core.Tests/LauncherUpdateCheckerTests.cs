using CameoIFV.Core.Github;
using CameoIFV.Core.Model;
using CameoIFV.Core.Update;
using Xunit;

namespace CameoIFV.Core.Tests;

public class LauncherUpdateCheckerTests
{
    // A stand-in release provider that returns a fixed list, ignoring source/platform (except the
    // empty-feed case is modelled by handing it an empty list).
    private sealed class FakeProvider : IReleaseProvider
    {
        private readonly IReadOnlyList<ResolvedRelease> _releases;
        public FakeProvider(params ResolvedRelease[] releases) => _releases = releases;

        public Task<IReadOnlyList<ResolvedRelease>> ListAsync(ReleaseSource source, string platform, CancellationToken cancellationToken)
            => Task.FromResult(_releases);
    }

    private static ResolvedRelease Release(string tag, bool prerelease = false) => new()
    {
        Channel = ReleaseChannel.Stable,
        TagName = tag,
        DisplayName = tag,
        PublishedAt = DateTimeOffset.UnixEpoch,
        Prerelease = prerelease,
        AssetUrl = new Uri($"https://example.test/{tag}.zip"),
        AssetName = $"Cameo-IFV-{tag}-win-x64.zip",
        AssetSize = 1,
        AssetId = 1,
        AssetUpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static Task<LauncherUpdate?> Check(Version current, bool includePrerelease, params ResolvedRelease[] releases)
        => new LauncherUpdateChecker(new FakeProvider(releases))
            .CheckAsync(current, "windows", includePrerelease, CancellationToken.None);

    [Fact]
    public async Task Returns_HighestNewerStableRelease()
    {
        var update = await Check(new Version(2, 0, 0), includePrerelease: false,
            Release("v2.0.0"), Release("v2.1.0"), Release("v2.0.5"));

        Assert.NotNull(update);
        Assert.Equal(new Version(2, 1, 0), update!.Version);
        Assert.Equal("v2.1.0", update.DisplayVersion);
    }

    [Fact]
    public async Task ReturnsNull_WhenUpToDate()
    {
        var update = await Check(new Version(2, 1, 0), includePrerelease: false,
            Release("v2.0.0"), Release("v2.1.0"));

        Assert.Null(update);
    }

    [Fact]
    public async Task OrdersNumerically_Not_Lexically()
    {
        // "v1.10.0" must beat "v1.2.0" — a string sort would get this wrong.
        var update = await Check(new Version(1, 2, 0), includePrerelease: false,
            Release("v1.2.0"), Release("v1.10.0"));

        Assert.Equal(new Version(1, 10, 0), update!.Version);
    }

    [Fact]
    public async Task SkipsPrereleases_ByDefault()
    {
        var update = await Check(new Version(2, 0, 0), includePrerelease: false,
            Release("v2.1.0", prerelease: true));

        Assert.Null(update);
    }

    [Fact]
    public async Task IncludesPrereleases_WhenOptedIn()
    {
        var update = await Check(new Version(2, 0, 0), includePrerelease: true,
            Release("v2.1.0-rc1", prerelease: true));

        Assert.Equal(new Version(2, 1, 0), update!.Version);
    }

    [Fact]
    public async Task IgnoresUnparseableTags()
    {
        var update = await Check(new Version(1, 0, 0), includePrerelease: false,
            Release("nightly"), Release("latest"));

        Assert.Null(update);
    }

    [Fact]
    public async Task ReturnsNull_WhenFeedEmpty()
    {
        var update = await Check(new Version(1, 0, 0), includePrerelease: false);

        Assert.Null(update);
    }
}
