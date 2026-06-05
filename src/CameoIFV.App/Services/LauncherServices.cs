using System;
using System.IO;
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

    public ModCatalog Catalog { get; }
    public IReleaseProvider ReleaseProvider { get; }
    public InstallOrchestrator Installer { get; private set; }
    public InstanceManager Instances { get; private set; }
    public LauncherPaths Paths { get; private set; }
    public string LibraryRoot => Paths.Root;

    /// <summary>Active platform key used to select per-OS assets from the catalog.</summary>
    public string Platform { get; } = OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsMacOS() ? "macos"
        : "unknown";

    public LauncherServices()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        _http.DefaultRequestHeaders.Add("User-Agent", "Cameo-IFV/0.1");

        _settingsStore = new LauncherSettingsStore();
        var settings = _settingsStore.Load();
        Paths = new LauncherPaths(_settingsStore.ResolveLibraryRoot(settings), _settingsStore.SettingsDir);
        Paths.EnsureBaseDirs();

        Catalog = LoadCatalog();
        ReleaseProvider = new GitHubReleaseProvider(_http, new ETagStore(Paths.ETagCacheFile));

        _factory = new UpdaterFactory(_http);
        (Installer, Instances) = CreateInstallServices(Paths);
    }

    public void SetLibraryRoot(string libraryRoot)
    {
        var resolved = Path.GetFullPath(Environment.ExpandEnvironmentVariables(libraryRoot));
        var paths = new LauncherPaths(resolved, _settingsStore.SettingsDir);
        paths.EnsureBaseDirs();

        _settingsStore.Save(new LauncherSettings(resolved));
        Paths = paths;
        (Installer, Instances) = CreateInstallServices(Paths);
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
