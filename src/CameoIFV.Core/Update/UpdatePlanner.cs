using CameoIFV.Core.Github;
using CameoIFV.Core.Model;
using CameoIFV.Core.Storage;

namespace CameoIFV.Core.Update;

/// <summary>
/// Turns a chosen <see cref="ResolvedRelease"/> into an <see cref="UpdatePlan"/>: picks the seed zip
/// (the previously downloaded zip for the same mod+channel, if present) and the output path.
/// </summary>
public sealed class UpdatePlanner
{
    private readonly LauncherPaths _paths;

    public UpdatePlanner(LauncherPaths paths) => _paths = paths;

    public UpdatePlan PlanFor(string modId, ResolvedRelease release)
    {
        var seed = _paths.SeedZip(modId, release.Channel);
        var hasSeed = File.Exists(seed);

        // Assemble into a distinct temp file — never the seed path, or zsync would truncate the very
        // file it reads as the seed. The orchestrator promotes this to the seed slot after success.
        var output = Path.Combine(_paths.DownloadsDir, $"{modId}-{release.Channel}-{release.TagName}.zip.part"
            .Replace(Path.DirectorySeparatorChar, '_'));

        return new UpdatePlan
        {
            AssetUrl = release.AssetUrl,
            AssetSize = release.AssetSize,
            ZsyncUrl = release.ZsyncUrl,
            SeedZipPath = hasSeed ? seed : null,
            OutputZipPath = output,
        };
    }

    /// <summary>Path the assembled zip is promoted to (and reused as the next seed).</summary>
    public string SeedSlotFor(string modId, ReleaseChannel channel) => _paths.SeedZip(modId, channel);
}
