using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CameoIFV.Core.Config;
using CameoIFV.Core.Github;
using CameoIFV.Core.Install;
using CameoIFV.Core.Interaction;
using CameoIFV.Core.Model;
using CameoIFV.Core.Storage;
using CameoIFV.Core.Update;

namespace CameoIFV.App.Services;

/// <summary>
/// Composition root: wires the Core layers together and exposes them to the view model.
/// One place owns the HttpClient and the writable per-user paths.
/// </summary>
public sealed class LauncherServices
{
    private readonly HttpClient _http;
    private readonly UpdaterFactory _factory;
    private readonly LauncherUpdateChecker _launcherChecker;
    private readonly LauncherSelfUpdater _selfUpdater;
    private readonly LauncherSettingsStore _settingsStore;
    private LauncherSettings _settings;

    public ModCatalog Catalog { get; }
    public IReleaseProvider ReleaseProvider { get; }
    public InstallOrchestrator Installer { get; private set; }
    public InstanceManager Instances { get; private set; }
    public LauncherPaths Paths { get; private set; }
    public ObservableCollection<string> LibraryRoots { get; } = new();
    public string LibraryRoot => Paths.Root;
    public LibraryCleanupResult LastCleanupResult { get; private set; } = new(0, 0, 0);

    /// <summary>Active platform key used to select per-OS assets from the catalog.</summary>
    public string Platform { get; } = OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsMacOS() ? "macos"
        : "unknown";

    public LauncherServices()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        _http.DefaultRequestHeaders.Add("User-Agent", "Cameo-IFV/1.0");

        _settingsStore = new LauncherSettingsStore();
        _settings = _settingsStore.Load();
        Paths = new LauncherPaths(_settingsStore.ResolveLibraryRoot(_settings), _settingsStore.SettingsDir);
        LastCleanupResult = LibraryCleanup.CleanInterruptedInstalls(Paths);
        SetKnownLibraryRoots(_settingsStore.ResolveKnownLibraryRoots(_settings));

        Catalog = LoadCatalog();
        ReleaseProvider = new GitHubReleaseProvider(_http, new ETagStore(Paths.ETagCacheFile));

        _factory = new UpdaterFactory(_http);
        (Installer, Instances) = CreateInstallServices(Paths);

        _launcherChecker = new LauncherUpdateChecker(ReleaseProvider);
        _selfUpdater = new LauncherSelfUpdater(new FullDownloadUpdater(_http));
        // Sweep the renamed-aside exe left by a previous in-place update, now that it's no longer mapped.
        var selfUpdate = BuildSelfUpdateContext();
        LauncherSelfUpdater.CleanupPreviousUpdate(selfUpdate.InstallDir, selfUpdate.ExecutableName);
    }

    /// <summary>The running launcher's version string, for display (e.g. "v2.0.0").</summary>
    public string CurrentVersionDisplay => AppVersion.Raw;

    /// <summary>
    /// Returns a newer launcher release if one exists, or null. Null also when the running version is
    /// unknown (e.g. a local dev build), so we never offer to "update" an unversioned build.
    /// </summary>
    public Task<LauncherUpdate?> CheckForLauncherUpdateAsync(bool includePrerelease, CancellationToken cancellationToken)
    {
        if (AppVersion.Current is null)
            return Task.FromResult<LauncherUpdate?>(null);

        return _launcherChecker.CheckAsync(AppVersion.Current, Platform, includePrerelease, cancellationToken);
    }

    /// <summary>
    /// Downloads and applies a launcher update in place, returning the new executable to relaunch.
    /// </summary>
    public Task<string> ApplyLauncherUpdateAsync(LauncherUpdate update, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
        => _selfUpdater.ApplyAsync(update, BuildSelfUpdateContext(), progress, cancellationToken);

    private SelfUpdateContext BuildSelfUpdateContext()
    {
        var scratch = Path.Combine(Paths.DownloadsDir, "launcher");
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            // Unusual host with no process path: assume the published exe beside the app base dir.
            var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return new SelfUpdateContext(baseDir, "Cameo-IFV.exe", scratch);
        }

        return new SelfUpdateContext(Path.GetDirectoryName(exePath)!, Path.GetFileName(exePath), scratch);
    }

    public void SetLibraryRoot(string libraryRoot)
    {
        var resolved = Path.GetFullPath(Environment.ExpandEnvironmentVariables(libraryRoot));
        var paths = new LauncherPaths(resolved, _settingsStore.SettingsDir);
        LastCleanupResult = LibraryCleanup.CleanInterruptedInstalls(paths);

        var roots = LibraryRoots
            .Append(resolved)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        SaveSettings(_settings with { LibraryRoot = resolved, LibraryRoots = roots });
        Paths = paths;
        (Installer, Instances) = CreateInstallServices(Paths);
        SetKnownLibraryRoots(roots);
    }

    public void RemoveLibraryRoot(string libraryRoot)
    {
        var removed = Path.GetFullPath(Environment.ExpandEnvironmentVariables(libraryRoot));
        var roots = LibraryRoots
            .Where(root => !StringComparer.OrdinalIgnoreCase.Equals(root, removed))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (roots.Count == 0)
            roots.Add(LauncherPaths.DefaultRoot());

        var activeRoot = StringComparer.OrdinalIgnoreCase.Equals(LibraryRoot, removed)
            ? roots[0]
            : LibraryRoot;

        var paths = new LauncherPaths(activeRoot, _settingsStore.SettingsDir);
        LastCleanupResult = LibraryCleanup.CleanInterruptedInstalls(paths);

        SaveSettings(_settings with { LibraryRoot = activeRoot, LibraryRoots = roots.ToArray() });
        Paths = paths;
        (Installer, Instances) = CreateInstallServices(Paths);
        SetKnownLibraryRoots(roots);
    }

    public SelectedChannelSettings? SelectedChannel => _settings.SelectedChannel;
    public string? SelectedModId => _settings.SelectedModId;

    public void SetSelectedFeed(ModDefinition mod, ReleaseSource source)
    {
        SaveSettings(_settings with
        {
            SelectedModId = mod.Id,
            SelectedChannel = new SelectedChannelSettings(mod.Id, source.Channel, source.Repository),
        });
    }

    /// <summary>
    /// Opens one of a project's user-data folders (replays, maps, saves, or the data root) in the
    /// host file manager, returning the resolved path. Uses the current library's support dir. When
    /// <paramref name="version"/> is given, version-namespaced folders deep-link to that install's
    /// subfolder if it exists.
    /// </summary>
    public string OpenProjectFolder(ModDefinition mod, ProjectFolderKind kind, string? version = null)
    {
        var path = new ProjectFolders(Paths).Resolve(mod, kind, version);
        FolderOpener.Open(path);
        return path;
    }

    private void SaveSettings(LauncherSettings settings)
    {
        _settings = settings;
        _settingsStore.Save(_settings);
    }

    private void SetKnownLibraryRoots(IEnumerable<string> roots)
    {
        var normalized = roots
            .Select(r => Path.GetFullPath(Environment.ExpandEnvironmentVariables(r)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Reconcile in place rather than Clear()+Add(): clearing the bound collection transiently
        // drops the ComboBox selection, which left the library dropdown blank after a switch.
        for (var i = LibraryRoots.Count - 1; i >= 0; i--)
        {
            if (!normalized.Contains(LibraryRoots[i], StringComparer.OrdinalIgnoreCase))
                LibraryRoots.RemoveAt(i);
        }

        foreach (var root in normalized)
        {
            if (!LibraryRoots.Contains(root, StringComparer.OrdinalIgnoreCase))
                LibraryRoots.Add(root);
        }
    }

    private (InstallOrchestrator Installer, InstanceManager Instances) CreateInstallServices(LauncherPaths paths)
    {
        var planner = new UpdatePlanner(paths);
        return (new InstallOrchestrator(paths, planner, _factory), new InstanceManager(paths));
    }

    private static ModCatalog LoadCatalog()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "catalog.default.json");
        return File.Exists(path) ? CatalogLoader.Load(path) : new ModCatalog();
    }
}
