using System.Text.Json;
using CameoIFV.Core.Install;
using CameoIFV.Core.Model;
using CameoIFV.Core.Storage;
using Xunit;

namespace CameoIFV.Core.Tests;

public class InstanceManagerTests
{
    private static ModDefinition CameoMod() => new()
    {
        Id = "cameo",
        DisplayName = "Cameo",
        LaunchExecutable = "CameoMod.exe",
    };

    private static void WriteInstance(LauncherPaths paths, string folder, string versionTag, DateTime? writeTimeUtc = null)
    {
        var dir = paths.InstanceDir("cameo", folder);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "CameoMod.exe"), "MZ");
        File.WriteAllText(
            Path.Combine(dir, InstallOrchestrator.MetadataFileName),
            JsonSerializer.Serialize(new InstallMetadata { ModId = "cameo", Tag = versionTag }));
        if (writeTimeUtc is { } t)
            Directory.SetLastWriteTimeUtc(dir, t);
    }

    [Fact]
    public void PromoteToSingleInstance_RenamesLatestToMain_AndKeepsRealVersion()
    {
        using var temp = new TempDir();
        var paths = new LauncherPaths(temp.Path);
        var mod = CameoMod();

        WriteInstance(paths, "playtest-20260601", "playtest-20260601", new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        WriteInstance(paths, "playtest-20260701", "playtest-20260701", new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));

        var manager = new InstanceManager(paths);
        manager.PromoteToSingleInstance(mod);

        var mainDir = paths.InstanceDir("cameo", InstallOrchestrator.SingleInstanceFolder);
        Assert.True(Directory.Exists(mainDir));
        Assert.False(Directory.Exists(paths.InstanceDir("cameo", "playtest-20260701"))); // newest got renamed
        Assert.True(Directory.Exists(paths.InstanceDir("cameo", "playtest-20260601")));  // older left in place

        // The "main" folder still reports the real version it was promoted from.
        var main = manager.ListInstalled(mod).Single(i => i.Tag == InstallOrchestrator.SingleInstanceFolder);
        Assert.Equal("Main (playtest-20260701)", main.DisplayVersion);
    }

    [Fact]
    public void PromoteToSingleInstance_IsNoOp_WhenLatestIsAlreadyMain()
    {
        using var temp = new TempDir();
        var paths = new LauncherPaths(temp.Path);

        WriteInstance(paths, InstallOrchestrator.SingleInstanceFolder, "playtest-20260701");
        new InstanceManager(paths).PromoteToSingleInstance(CameoMod());

        Assert.True(Directory.Exists(paths.InstanceDir("cameo", InstallOrchestrator.SingleInstanceFolder)));
    }

    [Fact]
    public void DemoteFromSingleInstance_RenamesMainBackToItsVersion()
    {
        using var temp = new TempDir();
        var paths = new LauncherPaths(temp.Path);
        var mod = CameoMod();

        WriteInstance(paths, InstallOrchestrator.SingleInstanceFolder, "playtest-20260701");
        new InstanceManager(paths).DemoteFromSingleInstance(mod);

        Assert.False(Directory.Exists(paths.InstanceDir("cameo", InstallOrchestrator.SingleInstanceFolder)));
        Assert.True(Directory.Exists(paths.InstanceDir("cameo", "playtest-20260701")));
    }

    [Fact]
    public void DemoteFromSingleInstance_LeavesMain_WhenThatVersionFolderAlreadyExists()
    {
        using var temp = new TempDir();
        var paths = new LauncherPaths(temp.Path);

        WriteInstance(paths, InstallOrchestrator.SingleInstanceFolder, "playtest-20260701");
        WriteInstance(paths, "playtest-20260701", "playtest-20260701");

        new InstanceManager(paths).DemoteFromSingleInstance(CameoMod());

        // Won't clobber an existing version folder; "main" stays put.
        Assert.True(Directory.Exists(paths.InstanceDir("cameo", InstallOrchestrator.SingleInstanceFolder)));
    }

    [Fact]
    public void ListInstalled_ReturnsRunnableAndNonRunnableInstances()
    {
        using var temp = new TempDir();
        var paths = new LauncherPaths(temp.Path);
        var mod = new ModDefinition
        {
            Id = "cameo",
            DisplayName = "Cameo",
            LaunchExecutable = "CameoMod.exe",
        };

        var runnable = paths.InstanceDir(mod.Id, "playtest-1");
        Directory.CreateDirectory(runnable);
        var exe = Path.Combine(runnable, "CameoMod.exe");
        File.WriteAllText(exe, "MZ");

        var notRunnable = paths.InstanceDir(mod.Id, "playtest-2");
        Directory.CreateDirectory(notRunnable);
        File.WriteAllText(Path.Combine(notRunnable, "readme.txt"), "no exe");

        var instances = new InstanceManager(paths).ListInstalled(mod);

        Assert.Equal(2, instances.Count);
        Assert.Contains(instances, i => i.Tag == "playtest-1" && i.IsRunnable && i.ExecutablePath == exe);
        Assert.Contains(instances, i => i.Tag == "playtest-2" && !i.IsRunnable && i.ExecutablePath is null);
    }

    [Theory]
    [InlineData("playtest-20260701", "playtest-20260701", "playtest-20260701")] // normal install: just the version
    [InlineData("main", "playtest-20260701", "Main (playtest-20260701)")]        // single instance: Main (version)
    [InlineData("main", null, "Main")]                                            // single instance, no metadata
    public void DisplayVersion_FormatsLabel(string folder, string? metadataTag, string expected)
    {
        var metadata = metadataTag is null ? null : new InstallMetadata { Tag = metadataTag };
        var instance = new InstalledInstance("cameo", folder, $"X:/{folder}", null, metadata);

        Assert.Equal(expected, instance.DisplayVersion);
    }

    [Fact]
    public void Delete_RemovesInstanceDirectory()
    {
        using var temp = new TempDir();
        var paths = new LauncherPaths(temp.Path);
        var dir = paths.InstanceDir("cameo", "playtest-1");
        Directory.CreateDirectory(dir);
        var instance = new InstalledInstance("cameo", "playtest-1", dir, null);

        new InstanceManager(paths).Delete(instance);

        Assert.False(Directory.Exists(dir));
    }
}
