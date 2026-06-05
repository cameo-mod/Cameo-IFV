namespace CameoIFV.Core.Update;

/// <summary>
/// Describes one update job: produce <see cref="OutputZipPath"/> for the target release.
/// The launcher builds this from a selected release; it never references zsyncnet directly.
/// </summary>
public sealed class UpdatePlan
{
    /// <summary>Direct download URL of the target release zip (GitHub asset URL; redirects are followed).</summary>
    public required Uri AssetUrl { get; init; }

    /// <summary>Total size of the target zip, for progress + sanity checks.</summary>
    public required long AssetSize { get; init; }

    /// <summary>
    /// URL of the companion .zsync control file, when the release publishes one. Null => the updater
    /// must fall back to a full download of <see cref="AssetUrl"/>.
    /// </summary>
    public Uri? ZsyncUrl { get; init; }

    /// <summary>
    /// Previously downloaded zip to seed the zsync diff from, if one exists locally. Null on first install.
    /// </summary>
    public string? SeedZipPath { get; init; }

    /// <summary>Where to write the assembled target zip.</summary>
    public required string OutputZipPath { get; init; }
}

public readonly record struct UpdateProgress(long BytesTransferred, long TotalBytes)
{
    public double Fraction => TotalBytes > 0 ? (double)BytesTransferred / TotalBytes : 0;
}

public enum UpdateMode
{
    FullDownload,
    IncrementalZsync,
}

/// <summary>
/// Produces the target release zip, transferring as few bytes as possible. Implementations:
/// zsync (incremental, when a control file + seed are available) and full-download (fallback).
/// </summary>
public interface IUpdater
{
    UpdateMode Mode { get; }

    Task UpdateAsync(UpdatePlan plan, IProgress<UpdateProgress>? progress, CancellationToken cancellationToken);
}
