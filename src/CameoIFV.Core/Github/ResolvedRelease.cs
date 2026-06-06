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

    public string TimelineLabel
    {
        get
        {
            var date = PublishedAt.LocalDateTime.ToString("yyyy-MM-dd");
            var relativeAge = FormatRelativeAge(DateTimeOffset.UtcNow - PublishedAt.ToUniversalTime());
            return relativeAge is null
                ? $"{Channel} • {date}"
                : $"{Channel} • {date} ({relativeAge})";
        }
    }

    private static string? FormatRelativeAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero)
            age = TimeSpan.Zero;

        if (age < TimeSpan.FromMinutes(1))
            return "just now";

        if (age < TimeSpan.FromHours(1))
        {
            var minutes = Math.Max(1, (int)Math.Floor(age.TotalMinutes));
            return minutes == 1 ? "1 minute ago" : $"{minutes} minutes ago";
        }

        if (age < TimeSpan.FromDays(1))
        {
            var hours = Math.Max(1, (int)Math.Floor(age.TotalHours));
            return hours == 1 ? "1 hour ago" : $"{hours} hours ago";
        }

        if (age < TimeSpan.FromDays(7))
        {
            var days = Math.Max(1, (int)Math.Floor(age.TotalDays));
            return days == 1 ? "1 day ago" : $"{days} days ago";
        }

        return null;
    }
}
