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
    /// Mod ids the user has opted OUT of "update in place" — i.e. wants kept as a folder per version
    /// under <c>instances/{modId}/</c>. "Update in place" (one fixed instance folder overwritten on
    /// each update: stable executable path for shortcuts, no version pile-up) is the <b>default</b>, so
    /// this is a deny-list: a mod NOT listed here is single-instance. Absent/empty means every mod
    /// updates in place.
    /// </summary>
    public string[]? MultiInstanceModIds { get; init; }

    /// <summary>Whether the given mod updates in place (the default) rather than keeping a folder per version.</summary>
    public bool IsSingleInstance(string modId)
        => !(MultiInstanceModIds?.Contains(modId, StringComparer.OrdinalIgnoreCase) ?? false);

    /// <summary>
    /// Records the per-mod "update in place" choice, returning the updated settings (or the same
    /// instance when nothing changed). Single-instance is the default, so only an explicit opt-out is
    /// stored.
    /// </summary>
    public LauncherSettings WithSingleInstance(string modId, bool enabled)
    {
        var current = MultiInstanceModIds ?? Array.Empty<string>();
        var optedOut = current.Contains(modId, StringComparer.OrdinalIgnoreCase);
        if (enabled == !optedOut)
            return this; // already in the requested state

        var updated = enabled
            ? current.Where(id => !string.Equals(id, modId, StringComparison.OrdinalIgnoreCase)).ToArray()
            : current.Append(modId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        return this with { MultiInstanceModIds = updated };
    }

    /// <summary>
    /// The last channel/feed the user picked for each mod, keyed by mod id. Lets the launcher restore
    /// each mod's own channel when switched back to (e.g. Combined Arms on Dev stays on Dev), instead
    /// of every mod sharing one global <see cref="SelectedChannel"/>.
    /// </summary>
    public SelectedChannelSettings[]? ModChannels { get; init; }

    /// <summary>The remembered channel/feed for one mod, or null if none has been chosen yet.</summary>
    public SelectedChannelSettings? ChannelFor(string modId)
    {
        if (ModChannels is not null)
            foreach (var c in ModChannels)
                if (string.Equals(c.ModId, modId, StringComparison.OrdinalIgnoreCase))
                    return c;

        // Back-compat: settings written before per-mod channels only stored the last-used one.
        return string.Equals(SelectedChannel?.ModId, modId, StringComparison.OrdinalIgnoreCase)
            ? SelectedChannel
            : null;
    }

    /// <summary>
    /// Records <paramref name="pref"/> as this mod's channel (upserting the per-mod map) and as the
    /// last-used selection, returning the updated settings.
    /// </summary>
    public LauncherSettings WithSelectedChannel(SelectedChannelSettings pref)
    {
        var map = (ModChannels ?? Array.Empty<SelectedChannelSettings>())
            .Where(c => !string.Equals(c.ModId, pref.ModId, StringComparison.OrdinalIgnoreCase))
            .Append(pref)
            .ToArray();

        return this with { SelectedModId = pref.ModId, SelectedChannel = pref, ModChannels = map };
    }

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
