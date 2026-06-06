using CameoIFV.Core.Model;

namespace CameoIFV.Core.Install;

public sealed class InstallMetadata
{
    public string? ModId { get; set; }
    public string? ModDisplayName { get; set; }
    public string? Tag { get; set; }
    public ReleaseChannel? Channel { get; set; }
    public string? AssetName { get; set; }
    public string? AssetUrl { get; set; }
    public DateTimeOffset? InstalledAt { get; set; }
    public string? ExecutablePath { get; set; }
    public long? ExtractedSize { get; set; }
}
