using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using CameoIFV.Core.Storage;

namespace CameoIFV.App.Services;

public sealed record LauncherSettings(string? LibraryRoot, string[]? LibraryRoots)
{
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

public sealed class LauncherSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string SettingsDir { get; } = LauncherPaths.DefaultRoot();
    public string SettingsFile => Path.Combine(SettingsDir, "settings.json");

    public LauncherSettings Load()
    {
        if (!File.Exists(SettingsFile))
            return new LauncherSettings(null, null);

        try
        {
            return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(SettingsFile), JsonOptions)
                   ?? new LauncherSettings(null, null);
        }
        catch
        {
            return new LauncherSettings(null, null);
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
}
