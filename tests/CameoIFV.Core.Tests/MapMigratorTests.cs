using CameoIFV.Core.Install;
using Xunit;

namespace CameoIFV.Core.Tests;

public class MapMigratorTests
{
    // maps/<modId>/<version> under the support dir, matching OpenRA's user-map layout.
    static string MapsDir(string support, string version)
        => Path.Combine(support, "maps", "cameo", version);

    [Fact]
    public void CarryForward_SeedsNewVersion_FromMostRecentPriorVersion()
    {
        using var temp = new TempDir();
        var support = Path.Combine(temp.Path, "support", "cameo");

        var old = MapsDir(support, "v1");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "a.oramap"), "map-a");
        File.WriteAllText(Path.Combine(old, "b.oramap"), "map-b");

        MapMigrator.CarryForward(support, "maps/cameo/v2");

        var dest = MapsDir(support, "v2");
        Assert.Equal("map-a", File.ReadAllText(Path.Combine(dest, "a.oramap")));
        Assert.Equal("map-b", File.ReadAllText(Path.Combine(dest, "b.oramap")));
        // Source is never touched.
        Assert.True(File.Exists(Path.Combine(old, "a.oramap")));
    }

    [Fact]
    public void CarryForward_PicksMostRecentPrior_NotAnOlderOne()
    {
        using var temp = new TempDir();
        var support = Path.Combine(temp.Path, "support", "cameo");

        var older = MapsDir(support, "v1");
        var newer = MapsDir(support, "v2");
        Directory.CreateDirectory(older);
        Directory.CreateDirectory(newer);
        File.WriteAllText(Path.Combine(older, "old.oramap"), "old");
        File.WriteAllText(Path.Combine(newer, "new.oramap"), "new");

        // Make v2 unambiguously the most-recently-used prior folder.
        Directory.SetLastWriteTimeUtc(older, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(newer, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        MapMigrator.CarryForward(support, "maps/cameo/v3");

        var dest = MapsDir(support, "v3");
        Assert.True(File.Exists(Path.Combine(dest, "new.oramap")));
        Assert.False(File.Exists(Path.Combine(dest, "old.oramap")));
    }

    [Fact]
    public void CarryForward_NeverOverwrites_AMapAlreadyInTheNewVersion()
    {
        using var temp = new TempDir();
        var support = Path.Combine(temp.Path, "support", "cameo");

        var old = MapsDir(support, "v1");
        var current = MapsDir(support, "v2");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(old, "shared.oramap"), "old-copy");
        File.WriteAllText(Path.Combine(current, "shared.oramap"), "edited-in-v2");

        MapMigrator.CarryForward(support, "maps/cameo/v2");

        Assert.Equal("edited-in-v2", File.ReadAllText(Path.Combine(current, "shared.oramap")));
    }

    [Fact]
    public void CarryForward_IsMarkerGated_AndDoesNotResurrectDeletedMaps()
    {
        using var temp = new TempDir();
        var support = Path.Combine(temp.Path, "support", "cameo");

        var old = MapsDir(support, "v1");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "a.oramap"), "map-a");

        MapMigrator.CarryForward(support, "maps/cameo/v2");
        var dest = MapsDir(support, "v2");
        Assert.True(File.Exists(Path.Combine(dest, "a.oramap")));

        // Player deletes the carried map in v2, then the launcher runs again (e.g. relaunch).
        File.Delete(Path.Combine(dest, "a.oramap"));
        MapMigrator.CarryForward(support, "maps/cameo/v2");

        // The marker means the second run is a no-op: the deletion sticks.
        Assert.False(File.Exists(Path.Combine(dest, "a.oramap")));
    }

    [Fact]
    public void CarryForward_OnFirstEverInstall_WritesMarkerAndCopiesNothing()
    {
        using var temp = new TempDir();
        var support = Path.Combine(temp.Path, "support", "cameo");

        MapMigrator.CarryForward(support, "maps/cameo/v1");

        var parent = Path.Combine(support, "maps", "cameo");
        Assert.True(File.Exists(Path.Combine(parent, MapMigrator.MarkerPrefix + "v1")));
    }

    [Fact]
    public void CarryForward_CopiesOpenMapFolders_NotJustOramapFiles()
    {
        using var temp = new TempDir();
        var support = Path.Combine(temp.Path, "support", "cameo");

        var old = MapsDir(support, "v1");
        var folderMap = Path.Combine(old, "my-map");
        Directory.CreateDirectory(folderMap);
        File.WriteAllText(Path.Combine(folderMap, "map.yaml"), "Title: My Map");

        MapMigrator.CarryForward(support, "maps/cameo/v2");

        var dest = Path.Combine(MapsDir(support, "v2"), "my-map", "map.yaml");
        Assert.Equal("Title: My Map", File.ReadAllText(dest));
    }

    [Fact]
    public void CarryForward_PutsMarkerInParent_NotInsideTheScannedVersionFolder()
    {
        using var temp = new TempDir();
        var support = Path.Combine(temp.Path, "support", "cameo");

        var old = MapsDir(support, "v1");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "a.oramap"), "map-a");

        MapMigrator.CarryForward(support, "maps/cameo/v2");

        // OpenRA scans maps/cameo/v2 and would log a failed-load for any stray file there.
        var dest = MapsDir(support, "v2");
        Assert.False(File.Exists(Path.Combine(dest, MapMigrator.MarkerPrefix + "v2")));
        var parent = Path.Combine(support, "maps", "cameo");
        Assert.True(File.Exists(Path.Combine(parent, MapMigrator.MarkerPrefix + "v2")));
    }
}
