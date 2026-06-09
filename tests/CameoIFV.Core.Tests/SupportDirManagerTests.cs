using CameoIFV.Core.Install;
using Xunit;

namespace CameoIFV.Core.Tests;

public class SupportDirManagerTests
{
    [Fact]
    public void Prepare_SeedsSharedRootFilesAndPerModSubtrees_OnFirstRun()
    {
        using var temp = new TempDir();
        var shared = Path.Combine(temp.Path, "AppData", "OpenRA");
        var support = Path.Combine(temp.Path, "support", "combined-arms");

        // Shared root file (mod-agnostic) + a per-mod map under the engine id "ca".
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "settings.yaml"), "Graphics:\n\tMode: Windowed\n");
        Directory.CreateDirectory(Path.Combine(shared, "maps", "ca", "playtest-1"));
        File.WriteAllText(Path.Combine(shared, "maps", "ca", "playtest-1", "a.oramap"), "map");
        // A different mod's data must NOT come across.
        Directory.CreateDirectory(Path.Combine(shared, "maps", "cameo"));
        File.WriteAllText(Path.Combine(shared, "maps", "cameo", "other.oramap"), "other");

        SupportDirManager.Prepare(support, engineModId: "ca", sharedSupportDir: shared);

        Assert.True(File.Exists(Path.Combine(support, "settings.yaml")));
        Assert.True(File.Exists(Path.Combine(support, "maps", "ca", "playtest-1", "a.oramap")));
        Assert.False(Directory.Exists(Path.Combine(support, "maps", "cameo")));
        Assert.True(File.Exists(Path.Combine(support, SupportDirManager.MigrationMarker)));
    }

    [Fact]
    public void Prepare_DoesNotReMigrate_OnceMarkerExists()
    {
        using var temp = new TempDir();
        var shared = Path.Combine(temp.Path, "AppData", "OpenRA");
        var support = Path.Combine(temp.Path, "support", "cameo");

        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "settings.yaml"), "first");

        SupportDirManager.Prepare(support, "cameo", shared);
        Assert.True(File.Exists(Path.Combine(support, "settings.yaml")));

        // Simulate the user changing settings inside the isolated dir, then add new shared data.
        File.WriteAllText(Path.Combine(support, "settings.yaml"), "user-edited");
        File.WriteAllText(Path.Combine(shared, "player.oraid"), "id");

        SupportDirManager.Prepare(support, "cameo", shared);

        // The second run is a no-op: the user's edit is preserved and new shared files are not pulled in.
        Assert.Equal("user-edited", File.ReadAllText(Path.Combine(support, "settings.yaml")));
        Assert.False(File.Exists(Path.Combine(support, "player.oraid")));
    }

    [Fact]
    public void Prepare_CreatesDirAndMarker_WhenNoSharedDataExists()
    {
        using var temp = new TempDir();
        var shared = Path.Combine(temp.Path, "does-not-exist");
        var support = Path.Combine(temp.Path, "support", "cameo");

        SupportDirManager.Prepare(support, "cameo", shared);

        Assert.True(Directory.Exists(support));
        Assert.True(File.Exists(Path.Combine(support, SupportDirManager.MigrationMarker)));
    }
}
