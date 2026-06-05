using CameoIFV.Core.Model;

namespace CameoIFV.Core.Github;

/// <summary>
/// A release after asset selection: the rest of the launcher consumes this, not raw GitHub JSON.
/// Carries the installable zip plus the optional .zsync sidecar for the active platform.
/// </summary>
public sealed class ResolvedRelease
{
    public required ReleaseChannel Channel { get; init; }
    public required string TagName { get; init; }
    public required string DisplayName { get; init; }
    public required DateTimeOffset PublishedAt { get; init; }
    public required bool Prerelease { get; init; }

    public required Uri AssetUrl { get; init; }
    public required string AssetName { get; init; }
    public required long AssetSize { get; init; }

    /// <summary>Identity used to detect in-place asset replacement (relevant for the CA mirror).</summary>
    public required long AssetId { get; init; }
    public required DateTimeOffset AssetUpdatedAt { get; init; }

    /// <summary>The companion .zsync control file, when the release publishes one.</summary>
    public Uri? ZsyncUrl { get; init; }

    public bool SupportsIncrementalUpdate => ZsyncUrl is not null;
}
