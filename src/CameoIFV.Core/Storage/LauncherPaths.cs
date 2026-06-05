using CameoIFV.Core.Model;

namespace CameoIFV.Core.Storage;

/// <summary>
/// Resolves all on-disk locations under a single writable per-user data root, so the launcher never
/// depends on its install directory being writable (the old launcher broke under Program Files).
///
/// Layout under the data root:
///   etags.json                         API conditional-request cache
///   seeds/{modId}/{channel}.zip        most-recent downloaded zip, reused as the zsync seed
///   instances/{modId}/{tag}/           extracted, runnable install for one version
///   downloads/                         scratch for in-flight zips before extraction
/// </summary>
public sealed class LauncherPaths
{
    public string ConfigRoot { get; }
    public string Root { get; }

    public LauncherPaths(string? rootOverride = null, string? configRootOverride = null)
    {
        Root = rootOverride
               ?? DefaultRoot();
        ConfigRoot = configRootOverride
                     ?? rootOverride
                     ?? DefaultRoot();
    }

    public string ETagCacheFile => Path.Combine(ConfigRoot, "etags.json");

    public string DownloadsDir => Path.Combine(Root, "downloads");

    public string InstancesDir => Path.Combine(Root, "instances");

    public string SeedZip(string modId, ReleaseChannel channel)
        => Path.Combine(Root, "seeds", modId, $"{channel}.zip".ToLowerInvariant());

    public string InstanceDir(string modId, string tag)
        => Path.Combine(InstancesDir, modId, SanitizeTag(tag));

    public void EnsureBaseDirs()
    {
        Directory.CreateDirectory(ConfigRoot);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(DownloadsDir);
    }

    public static string DefaultRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cameo-IFV");

    /// <summary>Tags become folder names; strip anything a path can't hold.</summary>
    private static string SanitizeTag(string tag)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(tag.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
