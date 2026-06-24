using System.IO.Compression;
using System.Linq;
using CameoIFV.Core.Github;
using CameoIFV.Core.Install;
using CameoIFV.Core.Model;
using CameoIFV.Core.Storage;
using CameoIFV.Core.Update;
using Xunit;

namespace CameoIFV.Core.Tests;

public class InstallOrchestratorTests
{
    /// <summary>Fake updater: writes a prebuilt zip to the plan's output path (stands in for download).</summary>
    private sealed class FakeUpdater : IUpdater, IUpdaterFactory
    {
        private readonly byte[] _zipBytes;
        public FakeUpdater(byte[] zipBytes) => _zipBytes = zipBytes;

        public UpdateMode Mode => UpdateMode.FullDownload;

        public IUpdater ForPlan(UpdatePlan plan) => this;

        public async Task UpdateAsync(UpdatePlan plan, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(plan.OutputZipPath)!);
            await File.WriteAllBytesAsync(plan.OutputZipPath, _zipBytes, cancellationToken);
            progress?.Report(new UpdateProgress(_zipBytes.Length, _zipBytes.Length));
        }
    }

    private static byte[] MakeZip(params (string name, string content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = archive.CreateEntry(name);
                using var w = new StreamWriter(entry.Open());
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    private static ModDefinition CameoMod() => new()
    {
        Id = "cameo",
        DisplayName = "Cameo",
        LaunchExecutable = "CameoMod.exe",
    };

    private static ResolvedRelease Release(string tag) => new()
    {
        Channel = ReleaseChannel.Stable,
        TagName = tag,
        DisplayName = tag,
        PublishedAt = DateTimeOffset.UtcNow,
        Prerelease = true,
        AssetUrl = new Uri("https://example/zip"),
        AssetName = $"CameoMod-{tag}-x64-winportable.zip",
        AssetSize = 0,
        AssetId = 1,
        AssetUpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Install_Extracts_LocatesExe_AndPromotesSeed()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ifv-install-{Guid.NewGuid():N}");
        try
        {
            var paths = new LauncherPaths(root);
            var planner = new UpdatePlanner(paths);
            var zip = MakeZip(("CameoMod.exe", "MZ"), ("data/rules.yaml", "x"));
            var orchestrator = new InstallOrchestrator(paths, planner, new FakeUpdater(zip));

            var mod = CameoMod();
            var release = Release("playtest-20260601");
            var result = await orchestrator.InstallAsync(mod, release, progress: null, default);

            // Extracted to an isolated instance dir, with the exe located.
            Assert.True(Directory.Exists(result.InstanceDir));
            Assert.True(File.Exists(Path.Combine(result.InstanceDir, "CameoMod.exe")));
            Assert.True(File.Exists(Path.Combine(result.InstanceDir, "data", "rules.yaml")));
            Assert.Equal(Path.Combine(result.InstanceDir, "CameoMod.exe"), result.ExecutablePath);

            // The assembled zip was promoted to the seed slot (and the temp .part is gone).
            var seed = planner.SeedSlotFor(mod.Id, release.Channel);
            Assert.True(File.Exists(seed));
            Assert.Empty(Directory.EnumerateFiles(paths.DownloadsDir, "*.part"));

            // InstanceManager sees it as runnable.
            var manager = new InstanceManager(paths);
            var installed = manager.ListInstalled(mod);
            var one = Assert.Single(installed);
            Assert.Equal("playtest-20260601", one.Tag);
            Assert.True(one.IsRunnable);
            Assert.NotNull(one.Metadata);
            Assert.Equal(mod.DisplayName, one.Metadata.ModDisplayName);
            Assert.Equal(release.TagName, one.Metadata.Tag);
            Assert.Equal(release.Channel, one.Metadata.Channel);
            Assert.Equal(release.AssetUrl.ToString(), one.Metadata.AssetUrl);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SingleInstance_UsesFixedFolder_AndOverwritesOnUpdate()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ifv-install-{Guid.NewGuid():N}");
        try
        {
            var paths = new LauncherPaths(root);
            var planner = new UpdatePlanner(paths);
            var mod = CameoMod();

            var v1 = new InstallOrchestrator(paths, planner, new FakeUpdater(MakeZip(("CameoMod.exe", "MZ"), ("marker.txt", "v1"))));
            var r1 = await v1.InstallAsync(mod, Release("playtest-20260601"), progress: null, default, singleInstance: true);

            var v2 = new InstallOrchestrator(paths, planner, new FakeUpdater(MakeZip(("CameoMod.exe", "MZ"), ("marker.txt", "v2"))));
            var r2 = await v2.InstallAsync(mod, Release("playtest-20260701"), progress: null, default, singleInstance: true);

            // Both updates landed in the same fixed "main" folder, which the second overwrote.
            Assert.Equal(r1.InstanceDir, r2.InstanceDir);
            Assert.Equal(InstallOrchestrator.SingleInstanceFolder, Path.GetFileName(r2.InstanceDir));
            Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(r2.InstanceDir, "marker.txt")));

            // Only one instance folder exists, and it reports the real version from metadata.
            var manager = new InstanceManager(paths);
            var one = Assert.Single(manager.ListInstalled(mod));
            Assert.Equal("playtest-20260701", one.DisplayVersion);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SingleInstance_PrunesOldPerVersionFolders_OfTheSameMod()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ifv-install-{Guid.NewGuid():N}");
        try
        {
            var paths = new LauncherPaths(root);
            var planner = new UpdatePlanner(paths);
            var mod = CameoMod();

            // Two pre-existing per-version installs (isolated mode).
            var iso = new InstallOrchestrator(paths, planner, new FakeUpdater(MakeZip(("CameoMod.exe", "MZ"))));
            await iso.InstallAsync(mod, Release("playtest-20260601"), progress: null, default);
            await iso.InstallAsync(mod, Release("playtest-20260608"), progress: null, default);
            Assert.Equal(2, Directory.EnumerateDirectories(Path.Combine(root, "instances", mod.Id)).Count());

            // The first in-place update reclaims them, leaving only "main".
            var inPlace = new InstallOrchestrator(paths, planner, new FakeUpdater(MakeZip(("CameoMod.exe", "MZ"))));
            await inPlace.InstallAsync(mod, Release("playtest-20260701"), progress: null, default, singleInstance: true);

            var remaining = Directory.EnumerateDirectories(Path.Combine(root, "instances", mod.Id)).Select(Path.GetFileName).ToArray();
            Assert.Equal(new[] { InstallOrchestrator.SingleInstanceFolder }, remaining);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SingleInstance_DoesNotPrune_WhenNewInstallHasNoRunnableExe()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ifv-install-{Guid.NewGuid():N}");
        try
        {
            var paths = new LauncherPaths(root);
            var planner = new UpdatePlanner(paths);
            var mod = CameoMod();

            var good = new InstallOrchestrator(paths, planner, new FakeUpdater(MakeZip(("CameoMod.exe", "MZ"))));
            await good.InstallAsync(mod, Release("playtest-20260601"), progress: null, default);

            // A new in-place install whose zip lacks the configured executable: not "fully usable".
            var noExe = new InstallOrchestrator(paths, planner, new FakeUpdater(MakeZip(("data/rules.yaml", "x"))));
            await noExe.InstallAsync(mod, Release("playtest-20260701"), progress: null, default, singleInstance: true);

            // The old, runnable version is kept as a fallback rather than pruned.
            var names = Directory.EnumerateDirectories(Path.Combine(root, "instances", mod.Id)).Select(Path.GetFileName).ToArray();
            Assert.Contains("playtest-20260601", names);
            Assert.Contains(InstallOrchestrator.SingleInstanceFolder, names);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SingleInstance_PruneNeverTouchesTheSupportDir()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ifv-install-{Guid.NewGuid():N}");
        try
        {
            var paths = new LauncherPaths(root);
            var planner = new UpdatePlanner(paths);
            var mod = CameoMod();

            // A custom map sitting in the support dir, as OpenRA would store it.
            var userMap = Path.Combine(paths.SupportDir(mod.Id), "maps", "cameo", "playtest-20260601", "mine.oramap");
            Directory.CreateDirectory(Path.GetDirectoryName(userMap)!);
            await File.WriteAllTextAsync(userMap, "my map");

            var iso = new InstallOrchestrator(paths, planner, new FakeUpdater(MakeZip(("CameoMod.exe", "MZ"))));
            await iso.InstallAsync(mod, Release("playtest-20260601"), progress: null, default);

            var inPlace = new InstallOrchestrator(paths, planner, new FakeUpdater(MakeZip(("CameoMod.exe", "MZ"))));
            await inPlace.InstallAsync(mod, Release("playtest-20260701"), progress: null, default, singleInstance: true);

            // Pruning removed the old instance folder but left the user's map untouched.
            Assert.Equal("my map", await File.ReadAllTextAsync(userMap));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Install_RejectsCorruptArchive()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ifv-install-{Guid.NewGuid():N}");
        try
        {
            var paths = new LauncherPaths(root);
            var planner = new UpdatePlanner(paths);
            var notAZip = System.Text.Encoding.UTF8.GetBytes("this is not a zip file");
            var orchestrator = new InstallOrchestrator(paths, planner, new FakeUpdater(notAZip));

            await Assert.ThrowsAnyAsync<Exception>(() =>
                orchestrator.InstallAsync(CameoMod(), Release("bad"), progress: null, default));

            // Failed install leaves no seed and no leftover temp part.
            Assert.False(File.Exists(planner.SeedSlotFor("cameo", ReleaseChannel.Stable)));
            Assert.Empty(Directory.EnumerateFiles(paths.DownloadsDir, "*.part"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedReinstall_PreservesExistingInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), $"ifv-install-{Guid.NewGuid():N}");
        try
        {
            var paths = new LauncherPaths(root);
            var planner = new UpdatePlanner(paths);
            var mod = CameoMod();
            var release = Release("playtest-20260601");

            var goodZip = MakeZip(("CameoMod.exe", "MZ"), ("marker.txt", "v1"));
            var goodInstall = new InstallOrchestrator(paths, planner, new FakeUpdater(goodZip));
            var result = await goodInstall.InstallAsync(mod, release, progress: null, default);
            var marker = Path.Combine(result.InstanceDir, "marker.txt");
            Assert.Equal("v1", await File.ReadAllTextAsync(marker));

            var notAZip = System.Text.Encoding.UTF8.GetBytes("this is not a zip file");
            var badInstall = new InstallOrchestrator(paths, planner, new FakeUpdater(notAZip));

            await Assert.ThrowsAnyAsync<Exception>(() =>
                badInstall.InstallAsync(mod, release, progress: null, default));

            Assert.True(Directory.Exists(result.InstanceDir));
            Assert.True(File.Exists(marker));
            Assert.Equal("v1", await File.ReadAllTextAsync(marker));

            var modInstancesRoot = Path.Combine(root, "instances", mod.Id);
            Assert.Empty(Directory.EnumerateDirectories(modInstancesRoot, "*.staging-*"));
            Assert.Empty(Directory.EnumerateDirectories(modInstancesRoot, "*.backup-*"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
