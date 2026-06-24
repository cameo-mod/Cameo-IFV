namespace CameoIFV.Core.Install;

/// <summary>
/// Carries a project's user maps forward when it updates to a new version.
///
/// OpenRA stores user/downloaded maps under a per-mod-VERSION subfolder (<c>maps/{modId}/{version}</c>),
/// so after an update the engine reads a fresh, empty folder and the player's custom maps vanish from
/// the in-game list until moved by hand. Because Cameo-IFV gives each project one support dir shared
/// across all its versions (see <see cref="Storage.LauncherPaths.SupportDir"/>), every prior version's
/// maps still sit on disk — this seeds the new version's folder from the most recent prior one.
///
/// Like <see cref="SupportDirManager"/> the operation is <b>copy-only</b> (sources stay intact, so an
/// older install keeps its maps and rolling back still works), <b>marker-gated</b> (runs once per
/// target version), and <b>never throws</b> (a failed carry-forward must not block launching).
/// </summary>
public static class MapMigrator
{
    /// <summary>Prefix of the per-version done-marker, written in the parent <c>maps/{modId}</c> dir.</summary>
    public const string MarkerPrefix = ".ifv-maps-migrated-";

    /// <summary>
    /// Seeds <paramref name="userMapRelativePath"/> (relative to <paramref name="supportDir"/>, e.g.
    /// <c>maps/cameo/playtest-20260622</c>) from the most-recently-used prior version folder, copying
    /// only maps not already present. Safe to call before every launch.
    /// </summary>
    public static void CarryForward(string supportDir, string userMapRelativePath)
    {
        if (string.IsNullOrWhiteSpace(userMapRelativePath))
            return;

        try
        {
            var relative = userMapRelativePath
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar);

            var target = Path.Combine(supportDir, relative);
            var parent = Path.GetDirectoryName(target);
            var versionName = Path.GetFileName(target.TrimEnd(Path.DirectorySeparatorChar));
            if (parent is null || versionName.Length == 0)
                return;

            // The marker lives in the parent maps/{modId} dir, never inside a version folder: OpenRA
            // scans the version folder and would log a "failed to load map" for any stray file there.
            var marker = Path.Combine(parent, MarkerPrefix + versionName);
            if (File.Exists(marker))
                return;

            var source = FindMostRecentPriorVersion(parent, target);
            if (source is not null)
                CopyMissingEntries(source, target);

            // Mark done even when nothing was copied, so we don't rescan the tree on every launch.
            Directory.CreateDirectory(parent);
            TryWriteMarker(marker);
        }
        catch { /* best effort: a failed carry-forward must never block launching */ }
    }

    /// <summary>
    /// The prior version folder the player most recently used that still holds maps. Picking only the
    /// single most-recent folder (not merging every old version) respects maps the player deleted in
    /// the version they were last on, rather than resurrecting them from an ancient folder.
    /// </summary>
    static string? FindMostRecentPriorVersion(string parent, string target)
    {
        if (!Directory.Exists(parent))
            return null;

        string? best = null;
        var bestTime = DateTime.MinValue;
        foreach (var dir in Directory.EnumerateDirectories(parent))
        {
            if (PathsEqual(dir, target) || !HasContent(dir))
                continue;

            var time = Directory.GetLastWriteTimeUtc(dir);
            if (time >= bestTime)
            {
                bestTime = time;
                best = dir;
            }
        }

        return best;
    }

    static bool HasContent(string dir)
    {
        try { return Directory.EnumerateFileSystemEntries(dir).Any(); }
        catch { return false; }
    }

    static void CopyMissingEntries(string source, string target)
    {
        Directory.CreateDirectory(target);

        // Maps are .oramap files or (rarely) open map folders; copy whichever, never overwriting a
        // same-named map the player already has in the new version.
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var dest = Path.Combine(target, Path.GetFileName(entry));
            if (File.Exists(dest) || Directory.Exists(dest))
                continue;

            try
            {
                if (Directory.Exists(entry))
                    CopyDir(entry, dest);
                else
                    File.Copy(entry, dest);
            }
            catch { /* skip one bad entry, keep carrying the rest */ }
        }
    }

    static void CopyDir(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dest, Path.GetRelativePath(source, dir)));

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(dest, Path.GetRelativePath(source, file)), overwrite: false);
    }

    static void TryWriteMarker(string marker)
    {
        try { File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("o")); }
        catch { /* best effort: a missing marker only means we re-scan next launch */ }
    }

    static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
