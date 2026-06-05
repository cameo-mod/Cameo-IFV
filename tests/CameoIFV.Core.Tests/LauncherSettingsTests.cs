using CameoIFV.Core.Storage;
using Xunit;

namespace CameoIFV.Core.Tests;

public class LauncherSettingsTests
{
    [Fact]
    public void KnownLibraryRoots_NormalizesAndDeduplicatesCaseInsensitively()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "Library");
        var settings = new LauncherSettings(
            root,
            new[] { root.ToUpperInvariant(), "", Path.Combine(temp.Path, "Other") },
            null,
            null);

        var roots = settings.KnownLibraryRoots();

        Assert.Equal(2, roots.Count);
        Assert.Contains(Path.GetFullPath(root), roots);
        Assert.Contains(Path.GetFullPath(Path.Combine(temp.Path, "Other")), roots);
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptySettings()
    {
        using var temp = new TempDir();
        var settings = new LauncherSettingsStore(temp.Path).Load();

        Assert.Null(settings.LibraryRoot);
        Assert.Null(settings.LibraryRoots);
        Assert.Null(settings.SelectedModId);
        Assert.Null(settings.SelectedChannel);
    }

    [Fact]
    public void Load_CorruptJson_ReturnsEmptySettings()
    {
        using var temp = new TempDir();
        var store = new LauncherSettingsStore(temp.Path);
        File.WriteAllText(store.SettingsFile, "{ not json");

        var settings = store.Load();

        Assert.Null(settings.LibraryRoot);
        Assert.Null(settings.LibraryRoots);
        Assert.Null(settings.SelectedModId);
        Assert.Null(settings.SelectedChannel);
    }

    [Fact]
    public void ResolveKnownLibraryRoots_SupportsLegacySingleRootSettings()
    {
        using var temp = new TempDir();
        var store = new LauncherSettingsStore(temp.Path);
        var libraryRoot = Path.Combine(temp.Path, "LegacyLibrary");
        File.WriteAllText(store.SettingsFile, $$"""
        {
          "LibraryRoot": "{{libraryRoot.Replace("\\", "\\\\")}}"
        }
        """);

        var settings = store.Load();
        var roots = store.ResolveKnownLibraryRoots(settings);

        var resolved = Path.GetFullPath(libraryRoot);
        Assert.Equal(resolved, store.ResolveLibraryRoot(settings));
        Assert.Single(roots);
        Assert.Equal(resolved, roots[0]);
    }

    [Fact]
    public void Save_And_Load_RoundTripsSettings()
    {
        using var temp = new TempDir();
        var store = new LauncherSettingsStore(temp.Path);
        var expected = new LauncherSettings(
            Path.Combine(temp.Path, "Library"),
            new[] { Path.Combine(temp.Path, "Library"), Path.Combine(temp.Path, "Other") },
            "cameo",
            null);

        store.Save(expected);
        var actual = store.Load();

        Assert.Equal(expected.LibraryRoot, actual.LibraryRoot);
        Assert.Equal(expected.LibraryRoots, actual.LibraryRoots);
        Assert.Equal(expected.SelectedModId, actual.SelectedModId);
    }
}
