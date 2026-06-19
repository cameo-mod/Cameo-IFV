using CameoIFV.Core.Github;
using CameoIFV.Core.Model;

namespace CameoIFV.Core.Update;

/// <summary>A newer launcher release than the one running, ready to download and apply.</summary>
public sealed record LauncherUpdate(Version Version, ResolvedRelease Release)
{
    /// <summary>The release tag as published, e.g. "v2.1.0", for display.</summary>
    public string DisplayVersion => Release.TagName;
}

/// <summary>
/// Decides whether a newer launcher release exists by treating Cameo-IFV itself as a release source
/// and reusing the same GitHub listing path the mods use (ETag-cached, rate-limit tolerant). Compares
/// the running <see cref="AppVersion"/> against each release tag and returns the highest newer one.
/// </summary>
public sealed class LauncherUpdateChecker
{
    public const string Repository = "cameo-mod/Cameo-IFV";

    /// <summary>Matches the portable zip the release workflow publishes (Cameo-IFV-&lt;ver&gt;-win-x64.zip).</summary>
    public const string WindowsAssetSuffix = "-win-x64.zip";

    private readonly IReleaseProvider _provider;
    private readonly ReleaseSource _source;

    public LauncherUpdateChecker(IReleaseProvider provider, ReleaseSource? source = null)
    {
        _provider = provider;
        _source = source ?? DefaultSource();
    }

    /// <summary>The launcher's own release feed. Windows-only today, matching the published builds.</summary>
    public static ReleaseSource DefaultSource() => new()
    {
        Channel = ReleaseChannel.Stable,
        Repository = Repository,
        Assets = new()
        {
            ["windows"] = new AssetFilter { AssetSuffix = WindowsAssetSuffix },
        },
    };

    /// <summary>
    /// Returns the highest release newer than <paramref name="current"/>, or null when up to date.
    /// Pre-releases are excluded unless <paramref name="includePrerelease"/> is set. Releases with an
    /// unparseable tag are ignored rather than guessed at.
    /// </summary>
    public async Task<LauncherUpdate?> CheckAsync(
        Version current, string platform, bool includePrerelease, CancellationToken cancellationToken)
    {
        var releases = await _provider.ListAsync(_source, platform, cancellationToken);

        LauncherUpdate? best = null;
        foreach (var release in releases)
        {
            if (release.Prerelease && !includePrerelease)
                continue;

            var version = AppVersion.Parse(release.TagName);
            if (version is null || version <= current)
                continue;

            if (best is null || version > best.Version)
                best = new LauncherUpdate(version, release);
        }

        return best;
    }
}
