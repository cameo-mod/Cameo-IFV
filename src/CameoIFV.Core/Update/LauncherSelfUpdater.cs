using System.IO.Compression;

namespace CameoIFV.Core.Update;

/// <summary>
/// Where the running launcher lives and where to stage an update. Supplied by the App layer from the
/// current process so Core stays free of process/AppContext globals and is unit-testable.
/// </summary>
public sealed record SelfUpdateContext(string InstallDir, string ExecutableName, string DownloadsDir);

/// <summary>
/// Replaces the running launcher with a newer release in place. Built for a portable, single-file
/// Windows app: the only locked on-disk file is the running .exe, which Windows lets us *rename*
/// while running — so we rename it aside, drop the new files in, and the caller relaunches. The old
/// exe (still mapped by the live process) is swept up on the next startup via
/// <see cref="CleanupPreviousUpdate"/>.
///
/// Does NOT relaunch or exit the process itself — that's the App layer's job — so this class is fully
/// testable offline. Download is delegated to an <see cref="IUpdater"/> (full download; the launcher
/// zip is small, so no zsync).
/// </summary>
public sealed class LauncherSelfUpdater
{
    /// <summary>Suffix for the renamed-aside previous executable, swept on next launch.</summary>
    public const string OldSuffix = ".old";

    private readonly IUpdater _downloader;

    public LauncherSelfUpdater(IUpdater downloader) => _downloader = downloader;

    /// <summary>
    /// Downloads, verifies, extracts, and swaps the update into place. Returns the path of the new
    /// executable for the caller to relaunch. Throws (leaving the running launcher intact) on any
    /// failure: a non-writable install dir, a bad archive, or a missing executable in the package.
    /// </summary>
    public async Task<string> ApplyAsync(
        LauncherUpdate update, SelfUpdateContext ctx, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken)
    {
        // Fail before touching anything if we can't write here (e.g. installed under Program Files).
        EnsureWritable(ctx.InstallDir);

        Directory.CreateDirectory(ctx.DownloadsDir);
        var zipPath = Path.Combine(ctx.DownloadsDir, "launcher-update.part");
        var stagingDir = Path.Combine(ctx.DownloadsDir, "launcher-staging-" + Guid.NewGuid().ToString("N"));

        try
        {
            var plan = new UpdatePlan
            {
                AssetUrl = update.Release.AssetUrl,
                AssetSize = update.Release.AssetSize,
                OutputZipPath = zipPath,
            };
            await _downloader.UpdateAsync(plan, progress, cancellationToken);

            VerifyArchiveContains(zipPath, ctx.ExecutableName);

            TryDeleteDir(stagingDir);
            Directory.CreateDirectory(stagingDir);
            ZipFile.ExtractToDirectory(zipPath, stagingDir);

            // The portable zip nests files under a top-level folder, so find the exe wherever it lands
            // and treat its directory as the package root to copy from.
            var newExe = LocateExecutable(stagingDir, ctx.ExecutableName)
                ?? throw new InvalidDataException($"Update package does not contain {ctx.ExecutableName}.");
            var packageRoot = Path.GetDirectoryName(newExe)!;

            SwapIntoPlace(packageRoot, ctx);

            TryDeleteDir(stagingDir);
            TryDelete(zipPath);

            return Path.Combine(ctx.InstallDir, ctx.ExecutableName);
        }
        catch
        {
            TryDelete(zipPath);
            TryDeleteDir(stagingDir);
            throw;
        }
    }

    /// <summary>
    /// Deletes the renamed-aside previous executable left by a prior in-place update. Safe to call on
    /// every startup; a no-op when there's nothing to sweep.
    /// </summary>
    public static void CleanupPreviousUpdate(string installDir, string executableName)
        => TryDelete(Path.Combine(installDir, executableName + OldSuffix));

    private static void SwapIntoPlace(string packageRoot, SelfUpdateContext ctx)
    {
        var runningExe = Path.Combine(ctx.InstallDir, ctx.ExecutableName);
        var oldExe = runningExe + OldSuffix;

        // Clear any stale .old from an earlier update so the rename target is free.
        TryDelete(oldExe);

        var renamedAside = false;
        if (File.Exists(runningExe))
        {
            // Windows permits renaming the image of a running process; the new exe then takes its place.
            File.Move(runningExe, oldExe);
            renamedAside = true;
        }

        try
        {
            foreach (var source in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(packageRoot, source);
                var destination = Path.Combine(ctx.InstallDir, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination, overwrite: true);
            }
        }
        catch
        {
            // If we never managed to write the new exe, put the old one back so the launcher still runs.
            if (renamedAside && !File.Exists(runningExe) && File.Exists(oldExe))
                File.Move(oldExe, runningExe);
            throw;
        }
    }

    private static void EnsureWritable(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".ifv-write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new UnauthorizedAccessException(
                $"Cannot update in place: '{dir}' is not writable ({ex.Message}). Move Cameo-IFV to a "
                + "writable folder (e.g. out of Program Files) or download the new version manually.", ex);
        }
    }

    private static void VerifyArchiveContains(string zipPath, string executableName)
    {
        if (!File.Exists(zipPath) || new FileInfo(zipPath).Length == 0)
            throw new InvalidDataException("Downloaded launcher archive is missing or empty.");

        using var archive = ZipFile.OpenRead(zipPath);
        var hasExe = archive.Entries.Any(e =>
            string.Equals(Path.GetFileName(e.FullName), executableName, StringComparison.OrdinalIgnoreCase));
        if (!hasExe)
            throw new InvalidDataException($"Downloaded launcher archive does not contain {executableName}.");
    }

    private static string? LocateExecutable(string root, string executableName)
        => Directory.EnumerateFiles(root, executableName, SearchOption.AllDirectories).FirstOrDefault();

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best effort */ }
    }
}
