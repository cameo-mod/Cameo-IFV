using CameoIFV.Core.Model;
using CameoIFV.Core.Storage;
using Xunit;

namespace CameoIFV.Core.Tests;

public class LauncherSettingsTests
{
    [Fact]
    public void WithSelectedChannel_RemembersChannelPerMod()
    {
        var settings = new LauncherSettings(null, null, null, null)
            .WithSelectedChannel(new SelectedChannelSettings("cameo", ReleaseChannel.Stable, "cameo-mod/Cameo-mod"))
            .WithSelectedChannel(new SelectedChannelSettings("combined-arms", ReleaseChannel.Dev, "darkademic/CAmod"));

        // Choosing a channel for CA must not disturb Cameo's remembered channel.
        Assert.Equal(ReleaseChannel.Stable, settings.ChannelFor("cameo")!.Channel);
        Assert.Equal(ReleaseChannel.Dev, settings.ChannelFor("combined-arms")!.Channel);
        Assert.Equal("darkademic/CAmod", settings.ChannelFor("combined-arms")!.Repository);
    }

    [Fact]
    public void WithSelectedChannel_UpsertsInsteadOfDuplicating()
    {
        var settings = new LauncherSettings(null, null, null, null)
            .WithSelectedChannel(new SelectedChannelSettings("combined-arms", ReleaseChannel.Stable, "Inq8/CAmod"))
            .WithSelectedChannel(new SelectedChannelSettings("combined-arms", ReleaseChannel.Dev, "darkademic/CAmod"));

        Assert.Single(settings.ModChannels!);
        Assert.Equal(ReleaseChannel.Dev, settings.ChannelFor("combined-arms")!.Channel);
    }

    [Fact]
    public void ChannelFor_FallsBackToLegacySingleSelection()
    {
        // Settings written before per-mod channels: only the single SelectedChannel is present.
        var legacy = new LauncherSettings(null, null, "combined-arms",
            new SelectedChannelSettings("combined-arms", ReleaseChannel.Dev, "darkademic/CAmod"));

        Assert.Equal(ReleaseChannel.Dev, legacy.ChannelFor("combined-arms")!.Channel);
        Assert.Null(legacy.ChannelFor("cameo"));
    }

    [Fact]
    public void ModChannels_SurviveSaveLoadRoundTrip()
    {
        using var temp = new TempDir();
        var store = new LauncherSettingsStore(temp.Path);
        var settings = new LauncherSettings(null, null, null, null)
            .WithSelectedChannel(new SelectedChannelSettings("combined-arms", ReleaseChannel.Dev, "darkademic/CAmod"));

        store.Save(settings);
        var loaded = store.Load();

        Assert.Equal(ReleaseChannel.Dev, loaded.ChannelFor("combined-arms")!.Channel);
    }

    [Fact]
    public void IsSingleInstance_DefaultsToOn_ForUnconfiguredMods()
    {
        var settings = new LauncherSettings(null, null, null, null);

        Assert.True(settings.IsSingleInstance("cameo"));
        Assert.True(settings.IsSingleInstance("combined-arms"));
    }

    [Fact]
    public void WithSingleInstance_OptOutAndBackIn_IsPerMod()
    {
        var settings = new LauncherSettings(null, null, null, null)
            .WithSingleInstance("cameo", false); // opt Cameo out to per-version

        Assert.False(settings.IsSingleInstance("cameo"));
        Assert.True(settings.IsSingleInstance("combined-arms")); // others stay on by default
        Assert.Contains("cameo", settings.MultiInstanceModIds!);

        settings = settings.WithSingleInstance("cameo", true); // back to update-in-place
        Assert.True(settings.IsSingleInstance("cameo"));
        Assert.DoesNotContain("cameo", settings.MultiInstanceModIds!);
    }

    [Fact]
    public void WithSingleInstance_NoChange_ReturnsSameInstance()
    {
        var settings = new LauncherSettings(null, null, null, null);

        // Enabling the already-default-on state is a no-op (lets the caller skip a redundant save).
        Assert.Same(settings, settings.WithSingleInstance("cameo", true));
    }

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
