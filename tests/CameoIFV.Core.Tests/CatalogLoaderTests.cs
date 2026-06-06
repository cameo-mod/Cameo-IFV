using System.Linq;
using CameoIFV.Core.Config;
using CameoIFV.Core.Model;
using Xunit;

namespace CameoIFV.Core.Tests;

public class CatalogLoaderTests
{
    private static string FindRepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate '{relative}' walking up from {AppContext.BaseDirectory}");
    }

    [Fact]
    public void DefaultCatalog_BindsCamelCaseAndStringEnums()
    {
        var catalog = CatalogLoader.Load(FindRepoFile(Path.Combine("config", "catalog.default.json")));

        // Mods, in order.
        Assert.Equal(
            new[]
            {
                "cameo",
                "combined-arms",
                "ymca",
                "opene2140",
                "openra-red-alert",
                "openra-tiberian-dawn",
                "openra-dune-2000",
                "shattered-paradise",
            },
            catalog.Mods.Select(m => m.Id));

        var cameo = catalog.Mods.Single(m => m.Id == "cameo");
        Assert.Equal("Cameo", cameo.DisplayName);
        Assert.Equal("CameoMod.exe", cameo.LaunchExecutable);
        // Cameo has a single feed — there is no separate Cameo dev channel (only CA has stable+dev).
        var cameoSource = Assert.Single(cameo.Sources);
        Assert.Equal("cameo-mod/Cameo-mod", cameoSource.Repository);
        Assert.Equal(ReleaseChannel.Stable, cameoSource.Channel);

        // Per-OS asset filter bound (the nested dictionary + suffixes).
        var win = cameo.Sources[0].Assets["windows"];
        Assert.Equal("-x64-winportable.zip", win.AssetSuffix);
        Assert.Equal("-x64-winportable.zip.zsync", win.ZsyncSuffix);

        // CA stable/dev live in different repos.
        var ca = catalog.Mods.Single(m => m.Id == "combined-arms");
        Assert.Equal("CombinedArms.exe", ca.LaunchExecutable);
        Assert.Equal("Inq8/CAmod", ca.Sources.Single(s => s.Channel == ReleaseChannel.Stable).Repository);
        Assert.Equal("darkademic/CAmod", ca.Sources.Single(s => s.Channel == ReleaseChannel.Dev).Repository);
        // CA has no zsync yet -> explicit null, exercising the full-download fallback path.
        Assert.Null(ca.Sources[0].Assets["windows"].ZsyncSuffix);

        var ymca = catalog.Mods.Single(m => m.Id == "ymca");
        Assert.Equal("You Must Construct Additional", ymca.DisplayName);
        Assert.Equal("YouMustConstructAdditional.exe", ymca.LaunchExecutable);
        var ymcaSource = Assert.Single(ymca.Sources);
        Assert.Equal(ReleaseChannel.Stable, ymcaSource.Channel);
        Assert.Equal("patrickwieth/YMCA", ymcaSource.Repository);
        Assert.Equal("-x64-winportable.zip", ymcaSource.Assets["windows"].AssetSuffix);
        Assert.Null(ymcaSource.Assets["windows"].ZsyncSuffix);

        var opene2140 = catalog.Mods.Single(m => m.Id == "opene2140");
        Assert.Equal("OpenE2140", opene2140.DisplayName);
        Assert.Equal("OpenE2140.exe", opene2140.LaunchExecutable);
        var opene2140Source = Assert.Single(opene2140.Sources);
        Assert.Equal(ReleaseChannel.Stable, opene2140Source.Channel);
        Assert.Equal("OpenE2140/OpenE2140", opene2140Source.Repository);
        Assert.Equal("-x64-winportable.zip", opene2140Source.Assets["windows"].AssetSuffix);
        Assert.Null(opene2140Source.Assets["windows"].ZsyncSuffix);

        AssertOpenRA(catalog, "openra-red-alert", "OpenRA: Red Alert", "RedAlert.exe");
        AssertOpenRA(catalog, "openra-tiberian-dawn", "OpenRA: Tiberian Dawn", "TiberianDawn.exe");
        AssertOpenRA(catalog, "openra-dune-2000", "OpenRA: Dune 2000", "Dune2000.exe");

        var shatteredParadise = catalog.Mods.Single(m => m.Id == "shattered-paradise");
        Assert.Equal("Shattered Paradise", shatteredParadise.DisplayName);
        Assert.Equal("ShatteredParadise.exe", shatteredParadise.LaunchExecutable);
        var shatteredParadiseSource = Assert.Single(shatteredParadise.Sources);
        Assert.Equal(ReleaseChannel.Stable, shatteredParadiseSource.Channel);
        Assert.Equal("ABrandau/Shattered-Paradise-SDK", shatteredParadiseSource.Repository);
        Assert.Equal("-x64-winportable.zip", shatteredParadiseSource.Assets["windows"].AssetSuffix);
        Assert.Null(shatteredParadiseSource.Assets["windows"].ZsyncSuffix);
    }

    private static void AssertOpenRA(ModCatalog catalog, string id, string displayName, string launchExecutable)
    {
        var mod = catalog.Mods.Single(m => m.Id == id);
        Assert.Equal(displayName, mod.DisplayName);
        Assert.Equal(launchExecutable, mod.LaunchExecutable);
        var source = Assert.Single(mod.Sources);
        Assert.Equal(ReleaseChannel.Stable, source.Channel);
        Assert.Equal("OpenRA/OpenRA", source.Repository);
        Assert.Equal("-x64-winportable.zip", source.Assets["windows"].AssetSuffix);
        Assert.Null(source.Assets["windows"].ZsyncSuffix);
    }
}
