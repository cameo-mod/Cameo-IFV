using System;
using System.IO;
using System.Text.Json;
using CameoIFV.Core.Storage;

namespace CameoIFV.App.Services;

public sealed record LauncherSettings(string? LibraryRoot);

public sealed class LauncherSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string SettingsDir { get; } = LauncherPaths.DefaultRoot();
    public string SettingsFile => Path.Combine(SettingsDir, "settings.json");

    public LauncherSettings Load()
    {
        if (!File.Exists(SettingsFile))
            return new LauncherSettings(null);

        try
        {
            return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(SettingsFile), JsonOptions)
                   ?? new LauncherSettings(null);
        }
        catch
        {
            return new LauncherSettings(null);
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
}
