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
    public ModCatalog Catalog { get; }
    public IReleaseProvider ReleaseProvider { get; }
    public InstallOrchestrator Installer { get; }
    public InstanceManager Instances { get; }

    /// <summary>Active platform key used to select per-OS assets from the catalog.</summary>
    public string Platform { get; } = OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsLinux() ? "linux"
        : OperatingSystem.IsMacOS() ? "macos"
        : "unknown";

    public LauncherServices()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        http.DefaultRequestHeaders.Add("User-Agent", "Cameo-IFV/0.1");

        var paths = new LauncherPaths();
        paths.EnsureBaseDirs();

        Catalog = LoadCatalog();
        ReleaseProvider = new GitHubReleaseProvider(http, new ETagStore(paths.ETagCacheFile));

        var planner = new UpdatePlanner(paths);
        var factory = new UpdaterFactory(http);
        Installer = new InstallOrchestrator(paths, planner, factory);
        Instances = new InstanceManager(paths);
    }

    private static ModCatalog LoadCatalog()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "catalog.default.json");
        return File.Exists(path) ? CatalogLoader.Load(path) : new ModCatalog();
    }
}
