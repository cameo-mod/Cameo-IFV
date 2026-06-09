using System.Collections.Generic;

namespace CameoIFV.Core.Model;

/// <summary>
/// Root of the launcher's configuration: the set of mods it knows how to install,
/// each defined purely as data so a new sister project is a config entry, not code.
/// </summary>
public sealed class ModCatalog
{
    public List<ModDefinition> Mods { get; set; } = new();
}

/// <summary>
/// A single mod (e.g. Cameo, Combined Arms). A mod draws releases from one or more
/// <see cref="ReleaseSource"/>s — typically a stable channel and a dev channel that may
/// live in different GitHub repositories.
/// </summary>
public sealed class ModDefinition
{
    /// <summary>Stable identifier used for cache/instance/support folders. Never displayed.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// The OpenRA-internal mod id (as in the mod's own mod.yaml), used only to namespace the
    /// per-mod subtrees (maps/Replays/Saves/Content) when migrating existing user data into the
    /// isolated support dir. Differs from <see cref="Id"/> for the official mods (e.g. catalog id
    /// "openra-red-alert" → engine id "ra") and CA ("combined-arms" → "ca"). Falls back to
    /// <see cref="Id"/> when null. Not needed for launching — the branded launcher supplies its
    /// own Game.Mod.
    /// </summary>
    public string? EngineModId { get; set; }

    /// <summary>Human-facing name shown in the UI.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Name of the executable to launch within an extracted instance, e.g. "CameoMod.exe".
    /// When null the launcher falls back to discovering a single top-level .exe.
    /// </summary>
    public string? LaunchExecutable { get; set; }

    public List<ReleaseSource> Sources { get; set; } = new();
}

public enum ReleaseChannel
{
    Stable,
    Dev,
}

/// <summary>
/// One GitHub releases feed for a mod. Cameo has one feed; CA's stable feed is
/// Inq8/CAmod and dev feed is darkademic/CAmod.
/// </summary>
public sealed class ReleaseSource
{
    public ReleaseChannel Channel { get; set; } = ReleaseChannel.Stable;

    /// <summary>GitHub "owner/repo", e.g. "cameo-mod/Cameo-mod" or "Inq8/CAmod".</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>
    /// Per-OS asset selection. Windows-first today, but the core stays OS-agnostic:
    /// the active platform key selects which asset suffix to match in a release.
    /// </summary>
    public Dictionary<string, AssetFilter> Assets { get; set; } = new();
}

/// <summary>
/// How to recognise the installable asset (and its companion zsync control file)
/// among a release's attachments, for a given platform.
/// </summary>
public sealed class AssetFilter
{
    /// <summary>Asset name suffix, e.g. "-x64-winportable.zip".</summary>
    public string AssetSuffix { get; set; } = string.Empty;

    /// <summary>
    /// Suffix of the companion zsync control file published alongside the asset,
    /// e.g. "-x64-winportable.zip.zsync". When absent for a release, the launcher
    /// falls back to a full download of <see cref="AssetSuffix"/>.
    /// </summary>
    public string? ZsyncSuffix { get; set; }
}
