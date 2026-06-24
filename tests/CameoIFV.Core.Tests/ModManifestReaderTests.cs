using CameoIFV.Core.Install;
using Xunit;

namespace CameoIFV.Core.Tests;

public class ModManifestReaderTests
{
    const string CameoManifest =
        "Metadata:\n" +
        "\tTitle: mod-title\n" +
        "\tVersion: playtest-20260622\n" +
        "\n" +
        "MapFolders:\n" +
        "\tcameo|maps: System\n" +
        "\t~^SupportDir|maps/cameo/playtest-20260622: User\n" +
        "\n" +
        "Rules:\n" +
        "\tcameo|rules/misc.yaml\n";

    // CA lists several System map folders before the User one, in a different mod-id folder.
    const string CaManifest =
        "Metadata:\n" +
        "\tVersion: 1.09-DevTest-15\n" +
        "MapFolders:\n" +
        "\tca|missions/main-campaign: System\n" +
        "\tca|maps: System\n" +
        "\t~^SupportDir|maps/ca/1.09-DevTest-15: User\n" +
        "Rules:\n" +
        "\tca|rules/misc.yaml\n";

    [Fact]
    public void TryGetUserMapFolder_ReturnsSupportRelativeUserPath()
    {
        using var temp = new TempDir();
        temp.Write(Path.Combine("mods", "cameo", "mod.yaml"), CameoManifest);

        var ok = ModManifestReader.TryGetUserMapFolder(temp.Path, "cameo", out var path);

        Assert.True(ok);
        Assert.Equal("maps/cameo/playtest-20260622", path);
    }

    [Fact]
    public void TryGetUserMapFolder_FindsUserEntry_AfterMultipleSystemEntries()
    {
        using var temp = new TempDir();
        temp.Write(Path.Combine("mods", "ca", "mod.yaml"), CaManifest);

        var ok = ModManifestReader.TryGetUserMapFolder(temp.Path, "ca", out var path);

        Assert.True(ok);
        Assert.Equal("maps/ca/1.09-DevTest-15", path);
    }

    [Fact]
    public void TryGetUserMapFolder_DoesNotReadADifferentModsManifest()
    {
        using var temp = new TempDir();
        // A bundle ships several manifests; asking for "cameo" must not match "cnc".
        temp.Write(Path.Combine("mods", "cnc", "mod.yaml"), CameoManifest);

        var ok = ModManifestReader.TryGetUserMapFolder(temp.Path, "cameo", out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryGetUserMapFolder_ReturnsFalse_WhenNoManifest()
    {
        using var temp = new TempDir();

        var ok = ModManifestReader.TryGetUserMapFolder(temp.Path, "cameo", out _);

        Assert.False(ok);
    }
}
