using System.IO.Compression;
using System.Text.Json;
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

public readonly record struct InstallProgress(InstallPhase Phase, long BytesTransferred, long TotalBytes, UpdateMode UpdateMode, string? Detail = null)
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
    public const string MetadataFileName = ".cameo-ifv-install.json";

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
        ReportDetail(progress, InstallPhase.Downloading, updater.Mode, $"""
        Preparing download for {mod.DisplayName} {release.TagName}.
        Mode: {updater.Mode}
        Asset URL: {plan.AssetUrl}
        Zsync URL: {plan.ZsyncUrl?.ToString() ?? "(none)"}
        Seed archive: {plan.SeedZipPath ?? "(none)"}
        Download part / target archive before verification: {plan.OutputZipPath}
        Expected archive size: {FormatBytes(plan.AssetSize)}
        """);
        var dlProgress = progress is null
            ? null
            : new Progress<UpdateProgress>(p => progress.Report(new InstallProgress(InstallPhase.Downloading, p.BytesTransferred, p.TotalBytes, updater.Mode)));
        await updater.UpdateAsync(plan, dlProgress, cancellationToken);

        var instanceDir = _paths.InstanceDir(mod.Id, release.TagName);
        var stagingDir = instanceDir + ".staging-" + Guid.NewGuid().ToString("N");

        try
        {
            // 2. Verify the assembled zip is structurally valid before we trust it.
            var archiveSize = File.Exists(plan.OutputZipPath) ? new FileInfo(plan.OutputZipPath).Length : 0;
            ReportDetail(progress, InstallPhase.Verifying, updater.Mode, $"""
            Verifying archive.
            Target archive: {plan.OutputZipPath}
            Archive size on disk: {FormatBytes(archiveSize)}
            """);
            VerifyZip(plan.OutputZipPath);

            // 3. Extract into a staging directory, then swap into place only after extraction succeeds.
            var instanceExists = Directory.Exists(instanceDir);
            ReportDetail(progress, InstallPhase.Extracting, updater.Mode, $"""
            Preparing extraction.
            Source archive: {plan.OutputZipPath}
            Staging directory to create: {stagingDir}
            Final instance directory: {instanceDir}
            Existing final instance: {(instanceExists ? "yes, it will be replaced after staging succeeds" : "no")}
            """);
            TryDeleteDir(stagingDir);
            Directory.CreateDirectory(stagingDir);
            ZipFile.ExtractToDirectory(plan.OutputZipPath, stagingDir);

            var stagedExe = ExecutableLocator.Locate(stagingDir, mod.LaunchExecutable);
            var extractedSize = DirectorySize(stagingDir);
            ReportDetail(progress, InstallPhase.Extracting, updater.Mode, $"""
            Extraction completed.
            Staging directory: {stagingDir}
            Extracted size: {FormatBytes(extractedSize)}
            Located executable: {stagedExe ?? "(none)"}
            """);
            SwapIntoPlace(stagingDir, instanceDir);

            // 4. Promote the assembled zip to the seed slot for the next update's diff.
            var seed = _planner.SeedSlotFor(mod.Id, release.Channel);
            var seedExists = File.Exists(seed);
            ReportDetail(progress, InstallPhase.Finalizing, updater.Mode, $"""
            Finalizing install.
            Final instance directory: {instanceDir}
            Seed archive path: {seed}
            Existing seed archive: {(seedExists ? "yes, it will be overwritten" : "no")}
            """);
            Directory.CreateDirectory(Path.GetDirectoryName(seed)!);
            File.Move(plan.OutputZipPath, seed, overwrite: true);

            var exe = stagedExe is null
                ? null
                : Path.Combine(instanceDir, Path.GetRelativePath(stagingDir, stagedExe));
            WriteMetadata(instanceDir, mod, release, exe, extractedSize);
            ReportDetail(progress, InstallPhase.Done, updater.Mode, $"""
            Install completed.
            Mod: {mod.DisplayName}
            Version: {release.TagName}
            Channel: {release.Channel}
            Installed at: {instanceDir}
            Executable: {exe ?? "(none)"}
            Retained seed archive: {seed}
            Extracted size: {FormatBytes(extractedSize)}
            """);
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

    private static void ReportDetail(IProgress<InstallProgress>? progress, InstallPhase phase, UpdateMode mode, string detail)
        => progress?.Report(new InstallProgress(phase, 0, 0, mode, detail.Trim()));

    private static long DirectorySize(string dir)
        => Directory.Exists(dir)
            ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length)
            : 0;

    private static string FormatBytes(long bytes)
    {
        const double kib = 1024;
        const double mib = kib * 1024;
        const double gib = mib * 1024;

        return bytes switch
        {
            >= (long)gib => $"{bytes / gib:F2} GB",
            >= (long)mib => $"{bytes / mib:F1} MB",
            >= (long)kib => $"{bytes / kib:F0} KB",
            _ => $"{bytes} B",
        };
    }

    private static void WriteMetadata(string instanceDir, ModDefinition mod, ResolvedRelease release, string? executablePath, long extractedSize)
    {
        var metadata = new InstallMetadata
        {
            ModId = mod.Id,
            ModDisplayName = mod.DisplayName,
            Tag = release.TagName,
            Channel = release.Channel,
            AssetName = release.AssetName,
            AssetUrl = release.AssetUrl.ToString(),
            InstalledAt = DateTimeOffset.UtcNow,
            ExecutablePath = executablePath,
            ExtractedSize = extractedSize,
        };

        File.WriteAllText(
            Path.Combine(instanceDir, MetadataFileName),
            JsonSerializer.Serialize(metadata, new JsonSerializerOptions { WriteIndented = true }));
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
