using System.IO.Compression;
using CameoIFV.Core.Github;
using CameoIFV.Core.Model;
using CameoIFV.Core.Storage;
using CameoIFV.Core.Update;

namespace CameoIFV.Core.Install;

public enum InstallPhase
{
    Downloading,
    Verifying,
    Extracting,
    Finalizing,
    Done,
}

public readonly record struct InstallProgress(InstallPhase Phase, long BytesTransferred, long TotalBytes)
{
    public double Fraction => TotalBytes > 0 ? (double)BytesTransferred / TotalBytes : 0;
}

public sealed record InstallResult(string InstanceDir, string? ExecutablePath);

/// <summary>
/// End-to-end install/update for one release: pick the cheapest updater, assemble the target zip,
/// verify it, extract it into an isolated instance dir, then promote the assembled zip to the seed
/// slot so the next update can diff against it. Each step is its own phase for UI progress.
/// </summary>
public sealed class InstallOrchestrator
{
    private readonly LauncherPaths _paths;
    private readonly UpdatePlanner _planner;
    private readonly IUpdaterFactory _updaters;

    public InstallOrchestrator(LauncherPaths paths, UpdatePlanner planner, IUpdaterFactory updaters)
    {
        _paths = paths;
        _planner = planner;
        _updaters = updaters;
    }

    public async Task<InstallResult> InstallAsync(
        ModDefinition mod,
        ResolvedRelease release,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        _paths.EnsureBaseDirs();

        var plan = _planner.PlanFor(mod.Id, release);
        var updater = _updaters.ForPlan(plan);

        // 1. Assemble the target zip (zsync incremental or full download).
        var dlProgress = progress is null
            ? null
            : new Progress<UpdateProgress>(p => progress.Report(new InstallProgress(InstallPhase.Downloading, p.BytesTransferred, p.TotalBytes)));
        await updater.UpdateAsync(plan, dlProgress, cancellationToken);

        var instanceDir = _paths.InstanceDir(mod.Id, release.TagName);
        var stagingDir = instanceDir + ".staging-" + Guid.NewGuid().ToString("N");

        try
        {
            // 2. Verify the assembled zip is structurally valid before we trust it.
            progress?.Report(new InstallProgress(InstallPhase.Verifying, 0, 0));
            VerifyZip(plan.OutputZipPath);

            // 3. Extract into a staging directory, then swap into place only after extraction succeeds.
            progress?.Report(new InstallProgress(InstallPhase.Extracting, 0, 0));
            TryDeleteDir(stagingDir);
            Directory.CreateDirectory(stagingDir);
            ZipFile.ExtractToDirectory(plan.OutputZipPath, stagingDir);

            var stagedExe = ExecutableLocator.Locate(stagingDir, mod.LaunchExecutable);
            SwapIntoPlace(stagingDir, instanceDir);

            // 4. Promote the assembled zip to the seed slot for the next update's diff.
            progress?.Report(new InstallProgress(InstallPhase.Finalizing, 0, 0));
            var seed = _planner.SeedSlotFor(mod.Id, release.Channel);
            Directory.CreateDirectory(Path.GetDirectoryName(seed)!);
            File.Move(plan.OutputZipPath, seed, overwrite: true);

            var exe = stagedExe is null
                ? null
                : Path.Combine(instanceDir, Path.GetRelativePath(stagingDir, stagedExe));
            progress?.Report(new InstallProgress(InstallPhase.Done, 0, 0));
            return new InstallResult(instanceDir, exe);
        }
        catch
        {
            // Don't leave a half-written temp zip or staging tree behind on failure.
            TryDelete(plan.OutputZipPath);
            TryDeleteDir(stagingDir);
            throw;
        }
    }

    private static void VerifyZip(string zipPath)
    {
        if (!File.Exists(zipPath) || new FileInfo(zipPath).Length == 0)
            throw new InvalidDataException("Assembled archive is missing or empty.");

        // Opening the central directory throws on a truncated/corrupt zip.
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count == 0)
            throw new InvalidDataException("Assembled archive contains no entries.");
    }

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

    private static void SwapIntoPlace(string stagingDir, string instanceDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(instanceDir)!);

        string? backupDir = null;
        if (Directory.Exists(instanceDir))
        {
            backupDir = instanceDir + ".backup-" + Guid.NewGuid().ToString("N");
            Directory.Move(instanceDir, backupDir);
        }

        try
        {
            Directory.Move(stagingDir, instanceDir);
        }
        catch
        {
            if (backupDir is not null && Directory.Exists(backupDir) && !Directory.Exists(instanceDir))
                Directory.Move(backupDir, instanceDir);

            throw;
        }

        if (backupDir is not null)
            TryDeleteDir(backupDir);
    }
}
