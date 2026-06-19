using CameoIFV.Core.Install;
using CameoIFV.Core.Model;
using CameoIFV.Core.Storage;
using Xunit;

namespace CameoIFV.Core.Tests;

public class ProjectFoldersTests
{
    static ModDefinition Mod() => new()
    {
        Id = "combined-arms",
        EngineModId = "ca",
        DisplayName = "Combined Arms",
    };

    [Theory]
    [InlineData(ProjectFolderKind.Data, "support/combined-arms")]
    [InlineData(ProjectFolderKind.Replays, "support/combined-arms/Replays/ca")]
    [InlineData(ProjectFolderKind.Maps, "support/combined-arms/maps/ca")]
    [InlineData(ProjectFolderKind.Saves, "support/combined-arms/Saves/ca")]
    public void Resolve_ReturnsEngineNamespacedFolder_AndCreatesIt(ProjectFolderKind kind, string relative)
    {
        using var temp = new TempDir();
        var folders = new ProjectFolders(new LauncherPaths(temp.Path));

        // No shared OpenRA data to migrate from, so this only prepares the support dir.
        var path = folders.Resolve(Mod(), kind, sharedSupportDir: Path.Combine(temp.Path, "no-such-source"));

        var expected = Path.Combine(temp.Path, relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.Equal(expected, path);
        Assert.True(Directory.Exists(path));
    }

    [Fact]
    public void Resolve_MigratesExistingReplays_OnFirstOpen()
    {
        using var temp = new TempDir();
        var shared = Path.Combine(temp.Path, "AppData", "OpenRA");
        Directory.CreateDirectory(Path.Combine(shared, "Replays", "ca"));
        File.WriteAllText(Path.Combine(shared, "Replays", "ca", "match.orarep"), "replay");

        var folders = new ProjectFolders(new LauncherPaths(temp.Path));
        var path = folders.Resolve(Mod(), ProjectFolderKind.Replays, sharedSupportDir: shared);

        // The player's existing replay is surfaced under the isolated support dir.
        Assert.True(File.Exists(Path.Combine(path, "match.orarep")));
    }

    [Fact]
    public void Resolve_DeepLinksIntoVersionSubfolder_WhenItExists()
    {
        using var temp = new TempDir();
        var paths = new LauncherPaths(temp.Path);
        // An existing replay folder for one installed version.
        var versioned = Path.Combine(paths.SupportDir("combined-arms"), "Replays", "ca", "playtest-20260614");
        Directory.CreateDirectory(versioned);

        var folders = new ProjectFolders(paths);
        var path = folders.Resolve(Mod(), ProjectFolderKind.Replays, version: "playtest-20260614",
            sharedSupportDir: Path.Combine(temp.Path, "none"));

        Assert.Equal(versioned, path);
    }

    [Fact]
    public void Resolve_FallsBackToParent_WhenVersionSubfolderMissing()
    {
        using var temp = new TempDir();
        var paths = new LauncherPaths(temp.Path);
        var folders = new ProjectFolders(paths);

        // Version given, but no subfolder for it (e.g. never recorded a replay, or version != tag).
        var path = folders.Resolve(Mod(), ProjectFolderKind.Replays, version: "playtest-99999999",
            sharedSupportDir: Path.Combine(temp.Path, "none"));

        var parent = Path.Combine(paths.SupportDir("combined-arms"), "Replays", "ca");
        Assert.Equal(parent, path);
        Assert.False(Directory.Exists(Path.Combine(parent, "playtest-99999999")));
    }

    [Fact]
    public void Resolve_IgnoresVersion_ForDataRoot()
    {
        using var temp = new TempDir();
        var paths = new LauncherPaths(temp.Path);
        var folders = new ProjectFolders(paths);

        var path = folders.Resolve(Mod(), ProjectFolderKind.Data, version: "playtest-20260614",
            sharedSupportDir: Path.Combine(temp.Path, "none"));

        Assert.Equal(paths.SupportDir("combined-arms"), path);
    }

    [Fact]
    public void Resolve_FallsBackToIdWhenEngineModIdMissing()
    {
        using var temp = new TempDir();
        var mod = new ModDefinition { Id = "cameo", DisplayName = "Cameo" };
        var folders = new ProjectFolders(new LauncherPaths(temp.Path));

        var path = folders.Resolve(mod, ProjectFolderKind.Maps, sharedSupportDir: Path.Combine(temp.Path, "none"));

        Assert.Equal(Path.Combine(temp.Path, "support", "cameo", "maps", "cameo"), path);
    }
}
