using System.Text.Json;
using CameoIFV.Core.Model;

namespace CameoIFV.Core.Storage;

public sealed record LauncherSettings(
    string? LibraryRoot,
    string[]? LibraryRoots,
    string? SelectedModId,
    SelectedChannelSettings? SelectedChannel)
{
    /// <summary>
    /// Mod ids the user has put in "update in place" mode: instead of a folder per version under
    /// <c>instances/{modId}/</c>, those mods install into a single fixed instance folder that the
    /// launcher overwrites on each update (stable executable path for desktop shortcuts, no version
    /// pile-up). Absent/empty means every mod keeps the default per-version isolation.
    /// </summary>
    public string[]? SingleInstanceModIds { get; init; }

    public IReadOnlyList<string> KnownLibraryRoots()
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(LibraryRoot))
            roots.Add(LibraryRoot);

        if (LibraryRoots is not null)
        {
            foreach (var root in LibraryRoots)
            {
                if (!string.IsNullOrWhiteSpace(root))
                    roots.Add(root);
            }
        }

        return roots
            .Select(NormalizeRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeRoot(string root)
        => Path.GetFullPath(Environment.ExpandEnvironmentVariables(root));
}

public sealed record SelectedChannelSettings(string ModId, ReleaseChannel Channel, string? Repository);

public sealed class LauncherSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public LauncherSettingsStore(string? settingsDirOverride = null)
    {
        SettingsDir = settingsDirOverride ?? LauncherPaths.DefaultRoot();
    }

    public string SettingsDir { get; }
    public string SettingsFile => Path.Combine(SettingsDir, "settings.json");

    public LauncherSettings Load()
    {
        if (!File.Exists(SettingsFile))
            return Empty();

        try
        {
            return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(SettingsFile), JsonOptions)
                   ?? Empty();
        }
        catch
        {
            return Empty();
        }
    }

    public void Save(LauncherSettings settings)
    {
        Directory.CreateDirectory(SettingsDir);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(settings, JsonOptions));
    }

    public string ResolveLibraryRoot(LauncherSettings settings)
        => string.IsNullOrWhiteSpace(settings.LibraryRoot)
            ? LauncherPaths.DefaultRoot()
            : Path.GetFullPath(Environment.ExpandEnvironmentVariables(settings.LibraryRoot));

    public IReadOnlyList<string> ResolveKnownLibraryRoots(LauncherSettings settings)
    {
        var roots = new List<string> { ResolveLibraryRoot(settings) };
        roots.AddRange(settings.KnownLibraryRoots());
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static LauncherSettings Empty() => new(null, null, null, null);
}
