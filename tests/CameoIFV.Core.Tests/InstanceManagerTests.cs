using CameoIFV.Core.Install;
using CameoIFV.Core.Model;
using CameoIFV.Core.Storage;
using Xunit;

namespace CameoIFV.Core.Tests;

public class InstanceManagerTests
{
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
