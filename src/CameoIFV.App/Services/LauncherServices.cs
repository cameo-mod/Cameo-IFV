using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using CameoIFV.Core.Config;
using CameoIFV.Core.Github;
using CameoIFV.Core.Install;
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
