using CameoIFV.Core.Model;
using CameoIFV.Core.Storage;
using Xunit;

namespace CameoIFV.Core.Tests;

public class LauncherPathsTests
{
    [Fact]
    public void RootOverride_UsesSameRootForLibraryAndConfig()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ifv-paths-{Guid.NewGuid():N}");
        var paths = new LauncherPaths(root);

        Assert.Equal(root, paths.Root);
        Assert.Equal(root, paths.ConfigRoot);
        Assert.Equal(Path.Combine(root, "etags.json"), paths.ETagCacheFile);
        Assert.Equal(Path.Combine(root, "downloads"), paths.DownloadsDir);
    }

    [Fact]
    public void SeparateConfigRoot_KeepsCacheOutOfLibrary()
    {
        var libraryRoot = Path.Combine(Path.GetTempPath(), $"ifv-library-{Guid.NewGuid():N}");
        var configRoot = Path.Combine(Path.GetTempPath(), $"ifv-config-{Guid.NewGuid():N}");
        var paths = new LauncherPaths(libraryRoot, configRoot);

        Assert.Equal(libraryRoot, paths.Root);
        Assert.Equal(configRoot, paths.ConfigRoot);
        Assert.Equal(Path.Combine(configRoot, "etags.json"), paths.ETagCacheFile);
        Assert.Equal(Path.Combine(libraryRoot, "downloads"), paths.DownloadsDir);
        Assert.Equal(Path.Combine(libraryRoot, "seeds", "cameo", "stable.zip"), paths.SeedZip("cameo", ReleaseChannel.Stable));
        Assert.Equal(Path.Combine(libraryRoot, "instances", "cameo", "playtest_2026_06_05"), paths.InstanceDir("cameo", "playtest:2026/06/05"));
    }
}
