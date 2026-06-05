using System.Net.Http;
using CameoIFV.Core.Update;
using Xunit;

namespace CameoIFV.Core.Tests;

public class UpdaterFactoryTests
{
    private static UpdatePlan Plan(Uri? zsync, string? seed) => new()
    {
        AssetUrl = new Uri("https://example/zip"),
        AssetSize = 1000,
        ZsyncUrl = zsync,
        SeedZipPath = seed,
        OutputZipPath = Path.Combine(Path.GetTempPath(), $"ifv-out-{Guid.NewGuid():N}.zip"),
    };

    [Fact]
    public void NoZsync_UsesFullDownload()
    {
        var factory = new UpdaterFactory(new HttpClient());
        Assert.IsType<FullDownloadUpdater>(factory.ForPlan(Plan(zsync: null, seed: null)));
    }

    [Fact]
    public void ZsyncButNoSeed_UsesFullDownload()
    {
        var factory = new UpdaterFactory(new HttpClient());
        // First install: release has a .zsync but there's no seed yet -> full download, not seedless zsync.
        var plan = Plan(zsync: new Uri("https://example/zsync"), seed: null);
        Assert.IsType<FullDownloadUpdater>(factory.ForPlan(plan));
    }

    [Fact]
    public void ZsyncWithMissingSeedFile_UsesFullDownload()
    {
        var factory = new UpdaterFactory(new HttpClient());
        var plan = Plan(zsync: new Uri("https://example/zsync"), seed: Path.Combine(Path.GetTempPath(), $"ifv-missing-{Guid.NewGuid():N}.zip"));
        Assert.IsType<FullDownloadUpdater>(factory.ForPlan(plan));
    }

    [Fact]
    public void ZsyncWithExistingSeed_UsesZsync()
    {
        var seed = Path.Combine(Path.GetTempPath(), $"ifv-seed-{Guid.NewGuid():N}.zip");
        File.WriteAllText(seed, "seed");
        try
        {
            var factory = new UpdaterFactory(new HttpClient());
            var plan = Plan(zsync: new Uri("https://example/zsync"), seed: seed);
            Assert.IsType<ZsyncUpdater>(factory.ForPlan(plan));
        }
        finally
        {
            File.Delete(seed);
        }
    }
}
