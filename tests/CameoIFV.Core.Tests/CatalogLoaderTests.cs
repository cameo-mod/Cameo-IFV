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

        // Two mods, in order.
        Assert.Equal(new[] { "cameo", "combined-arms" }, catalog.Mods.Select(m => m.Id));

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
    }
}
