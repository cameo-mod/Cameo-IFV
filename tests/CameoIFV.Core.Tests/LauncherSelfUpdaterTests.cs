using System.IO.Compression;
using CameoIFV.Core.Github;
using CameoIFV.Core.Model;
using CameoIFV.Core.Update;
using Xunit;

namespace CameoIFV.Core.Tests;

public class LauncherSelfUpdaterTests
{
    private const string ExeName = "Cameo-IFV.exe";

    // Stand-in downloader: copies a pre-built zip to the planned output path instead of hitting GitHub.
    private sealed class FakeDownloader : IUpdater
    {
        private readonly string _sourceZip;
        public FakeDownloader(string sourceZip) => _sourceZip = sourceZip;
        public UpdateMode Mode => UpdateMode.FullDownload;

        public Task UpdateAsync(UpdatePlan plan, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(plan.OutputZipPath)!);
            File.Copy(_sourceZip, plan.OutputZipPath, overwrite: true);
            return Task.CompletedTask;
        }
    }

    private static LauncherUpdate Update() => new(new Version(2, 1, 0), new ResolvedRelease
    {
        Channel = ReleaseChannel.Stable,
        TagName = "v2.1.0",
        DisplayName = "v2.1.0",
        PublishedAt = DateTimeOffset.UnixEpoch,
        Prerelease = false,
        AssetUrl = new Uri("https://example.test/Cameo-IFV-v2.1.0-win-x64.zip"),
        AssetName = "Cameo-IFV-v2.1.0-win-x64.zip",
        AssetSize = 0,
        AssetId = 1,
        AssetUpdatedAt = DateTimeOffset.UnixEpoch,
    });

    // Builds a portable-style zip: files nested under a top-level "Cameo-IFV/" folder, as the release
    // workflow produces. Entries is name -> contents.
    private static string BuildPackageZip(TempDir temp, string name, Dictionary<string, string> entries)
    {
        var zipPath = Path.Combine(temp.Path, name);
        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (entryName, contents) in entries)
        {
            var entry = archive.CreateEntry("Cameo-IFV/" + entryName);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(contents);
        }

        return zipPath;
    }

    private static (string InstallDir, SelfUpdateContext Ctx) FreshInstall(TempDir temp, string exeContents)
    {
        var installDir = Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(installDir);
        File.WriteAllText(Path.Combine(installDir, ExeName), exeContents);
        File.WriteAllText(Path.Combine(installDir, "catalog.default.json"), "OLD-CATALOG");

        var ctx = new SelfUpdateContext(installDir, ExeName, Path.Combine(temp.Path, "downloads"));
        return (installDir, ctx);
    }

    [Fact]
    public async Task ApplyAsync_SwapsExe_KeepsOldAside_AndCopiesNewFiles()
    {
        using var temp = new TempDir();
        var (installDir, ctx) = FreshInstall(temp, exeContents: "OLD-EXE");
        var zip = BuildPackageZip(temp, "update.zip", new()
        {
            [ExeName] = "NEW-EXE",
            ["catalog.default.json"] = "NEW-CATALOG",
            ["README.txt"] = "readme",
        });

        var updater = new LauncherSelfUpdater(new FakeDownloader(zip));
        var newExePath = await updater.ApplyAsync(Update(), ctx, null, CancellationToken.None);

        Assert.Equal(Path.Combine(installDir, ExeName), newExePath);
        Assert.Equal("NEW-EXE", File.ReadAllText(Path.Combine(installDir, ExeName)));
        Assert.Equal("NEW-CATALOG", File.ReadAllText(Path.Combine(installDir, "catalog.default.json")));
        Assert.Equal("readme", File.ReadAllText(Path.Combine(installDir, "README.txt")));
        // The previous exe is preserved aside for the next-launch sweep.
        Assert.Equal("OLD-EXE", File.ReadAllText(Path.Combine(installDir, ExeName + LauncherSelfUpdater.OldSuffix)));
    }

    [Fact]
    public async Task ApplyAsync_Throws_AndLeavesExeIntact_WhenArchiveMissingExe()
    {
        using var temp = new TempDir();
        var (installDir, ctx) = FreshInstall(temp, exeContents: "OLD-EXE");
        // A package with no executable in it.
        var zip = BuildPackageZip(temp, "bad.zip", new() { ["README.txt"] = "only docs" });

        var updater = new LauncherSelfUpdater(new FakeDownloader(zip));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => updater.ApplyAsync(Update(), ctx, null, CancellationToken.None));

        // Verification happens before any rename, so the running exe is untouched and no .old appears.
        Assert.Equal("OLD-EXE", File.ReadAllText(Path.Combine(installDir, ExeName)));
        Assert.False(File.Exists(Path.Combine(installDir, ExeName + LauncherSelfUpdater.OldSuffix)));
    }

    [Fact]
    public async Task ApplyAsync_Throws_WhenInstallDirNotWritable()
    {
        using var temp = new TempDir();
        // Use a FILE where a directory is expected: CreateDirectory on it fails, so the write-probe trips.
        var asFile = Path.Combine(temp.Path, "not-a-dir");
        File.WriteAllText(asFile, "x");
        var ctx = new SelfUpdateContext(asFile, ExeName, Path.Combine(temp.Path, "downloads"));
        var zip = BuildPackageZip(temp, "update.zip", new() { [ExeName] = "NEW-EXE" });

        var updater = new LauncherSelfUpdater(new FakeDownloader(zip));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => updater.ApplyAsync(Update(), ctx, null, CancellationToken.None));
    }

    [Fact]
    public void CleanupPreviousUpdate_RemovesOldExe()
    {
        using var temp = new TempDir();
        var installDir = Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(installDir);
        var oldExe = Path.Combine(installDir, ExeName + LauncherSelfUpdater.OldSuffix);
        File.WriteAllText(oldExe, "stale");

        LauncherSelfUpdater.CleanupPreviousUpdate(installDir, ExeName);

        Assert.False(File.Exists(oldExe));
    }

    [Fact]
    public void CleanupPreviousUpdate_IsNoOp_WhenNothingToSweep()
    {
        using var temp = new TempDir();
        var installDir = Path.Combine(temp.Path, "app");
        Directory.CreateDirectory(installDir);

        // Should not throw when there's no .old present.
        LauncherSelfUpdater.CleanupPreviousUpdate(installDir, ExeName);
    }
}
