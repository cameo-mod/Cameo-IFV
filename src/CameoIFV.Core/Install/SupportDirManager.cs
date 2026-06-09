namespace CameoIFV.Core.Install;

/// <summary>
/// Prepares a per-project OpenRA support directory and, the first time a project is launched under
/// the isolation scheme, seeds it from the shared platform support dir (<c>%AppData%\OpenRA</c>) so
/// existing users keep their settings/maps/replays/saves.
///
/// The migration is <b>copy-only</b> and <b>marker-gated</b>: the original shared data is never
/// moved or deleted (so rolling back to an older launcher that still reads <c>%AppData%\OpenRA</c>
/// keeps working), and once the marker is written the scan is skipped on every later launch. It is
/// therefore safe to call <see cref="Prepare"/> before every launch.
/// </summary>
public static class SupportDirManager
{
    public const string MigrationMarker = ".ifv-migrated";

    /// <summary>Root-level files shared by all OpenRA mods, copied verbatim (mod-agnostic).</summary>
    static readonly string[] SharedRootFiles = { "settings.yaml", "player.oraid" };

    /// <summary>Per-mod subtrees, each namespaced by the OpenRA-internal mod id under the root.</summary>
    static readonly string[] PerModSubDirs = { "maps", "Replays", "Saves", "Content" };

    /// <summary>
    /// Ensures <paramref name="supportDir"/> exists and, on first use, seeds it from
    /// <paramref name="sharedSupportDir"/> (defaults to the platform <c>%AppData%\OpenRA</c>).
    /// Returns the prepared support dir path. Never throws for migration problems — a failed copy
    /// must not block launching the game.
    /// </summary>
    /// <param name="supportDir">The isolated per-project support dir to prepare.</param>
    /// <param name="engineModId">OpenRA-internal mod id used to pick the per-mod subtrees to copy.</param>
    /// <param name="sharedSupportDir">Migration source; defaults to <c>%AppData%\OpenRA</c>.</param>
    public static string Prepare(string supportDir, string engineModId, string? sharedSupportDir = null)
    {
        Directory.CreateDirectory(supportDir);

        var marker = Path.Combine(supportDir, MigrationMarker);
        if (File.Exists(marker))
            return supportDir;

        var source = sharedSupportDir ?? DefaultSharedSupportDir();
        if (source is not null && Directory.Exists(source) && !PathsEqual(source, supportDir))
            Migrate(source, supportDir, engineModId);

        // Write the marker even when nothing was copied, so we don't re-scan the shared dir every launch.
        TryWriteMarker(marker);
        return supportDir;
    }

    static void Migrate(string source, string dest, string engineModId)
    {
        foreach (var file in SharedRootFiles)
            TryCopyFile(Path.Combine(source, file), Path.Combine(dest, file));

        // Map-generator preferences are a per-mod root file (map-generator-<mod>.yaml).
        var mapGen = "map-generator-" + engineModId + ".yaml";
        TryCopyFile(Path.Combine(source, mapGen), Path.Combine(dest, mapGen));

        foreach (var sub in PerModSubDirs)
            TryCopyDir(Path.Combine(source, sub, engineModId), Path.Combine(dest, sub, engineModId));
    }

    /// <summary>The platform's shared OpenRA support dir on Windows: <c>%AppData%\OpenRA</c>.</summary>
    static string DefaultSharedSupportDir()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OpenRA");

    static void TryWriteMarker(string marker)
    {
        try { File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("o")); }
        catch { /* best effort: a missing marker only means we re-scan next launch */ }
    }

    static void TryCopyFile(string source, string dest)
    {
        try
        {
            if (!File.Exists(source) || File.Exists(dest))
                return;

            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(source, dest);
        }
        catch { /* best effort: never block launch on a migration copy */ }
    }

    static void TryCopyDir(string source, string dest)
    {
        try
        {
            if (!Directory.Exists(source) || Directory.Exists(dest))
                return;

            Directory.CreateDirectory(dest);
            foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));

            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                File.Copy(file, Path.Combine(dest, Path.GetRelativePath(source, file)), overwrite: false);
        }
        catch { /* best effort: partial copies are acceptable, the original stays intact */ }
    }

    static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
