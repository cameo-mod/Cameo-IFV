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

    [Fact]
    public void Cleanup_RemovesInterruptedInstallScratchOnly()
    {
        using var temp = new TempDir();
        var paths = new LauncherPaths(temp.Path);
        paths.EnsureBaseDirs();

        var partial = Path.Combine(paths.DownloadsDir, "cameo-stable-playtest.zip.part");
        File.WriteAllText(partial, "partial");

        var staging = Path.Combine(paths.InstancesDir, "cameo", "playtest.staging-abc");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "temp.txt"), "temp");

        var backup = Path.Combine(paths.InstancesDir, "cameo", "playtest.backup-def");
        Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup, "old.txt"), "old");

        var installed = paths.InstanceDir("cameo", "playtest");
        Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(installed, "CameoMod.exe"), "MZ");

        var seed = paths.SeedZip("cameo", ReleaseChannel.Stable);
        Directory.CreateDirectory(Path.GetDirectoryName(seed)!);
        File.WriteAllText(seed, "seed");

        var result = LibraryCleanup.CleanInterruptedInstalls(paths);

        Assert.Equal(1, result.PartialDownloadsDeleted);
        Assert.Equal(1, result.StagingDirectoriesDeleted);
        Assert.Equal(1, result.BackupDirectoriesDeleted);
        Assert.False(File.Exists(partial));
        Assert.False(Directory.Exists(staging));
        Assert.False(Directory.Exists(backup));
        Assert.True(Directory.Exists(installed));
        Assert.True(File.Exists(seed));

    }

    [Fact]
    public void Cleanup_PreservesNestedDirectoriesThatLookLikeScratch()
    {
        using var temp = new TempDir();
        var paths = new LauncherPaths(temp.Path);
        var installed = paths.InstanceDir("cameo", "playtest");
        var nested = Path.Combine(installed, "mods", "example.staging-data");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "keep.txt"), "keep");

        var topLevelScratch = Path.Combine(paths.InstancesDir, "cameo", "playtest.staging-abandoned");
        Directory.CreateDirectory(topLevelScratch);

        var result = LibraryCleanup.CleanInterruptedInstalls(paths);

        Assert.Equal(1, result.StagingDirectoriesDeleted);
        Assert.False(Directory.Exists(topLevelScratch));
        Assert.True(Directory.Exists(nested));
        Assert.True(File.Exists(Path.Combine(nested, "keep.txt")));
    }
}
