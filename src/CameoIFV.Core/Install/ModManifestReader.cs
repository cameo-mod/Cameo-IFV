namespace CameoIFV.Core.Install;

/// <summary>
/// Reads the few facts the launcher needs from an installed instance's <c>mod.yaml</c> without a full
/// YAML parser. Today that is the engine's <b>user</b> map folder — the version-namespaced directory
/// (<c>^SupportDir|maps/{modId}/{version}</c>) that OpenRA reads custom/downloaded maps from — which
/// <see cref="MapMigrator"/> uses to carry maps forward across an update.
/// </summary>
public static class ModManifestReader
{
    /// <summary>
    /// Finds the relative path (under the support dir) of the instance's <c>User</c>-classified map
    /// folder, e.g. <c>maps/cameo/playtest-20260622</c>. Returns false when the manifest can't be read
    /// or the user maps live outside the support dir (nothing for us to manage).
    /// </summary>
    public static bool TryGetUserMapFolder(string instanceDir, string engineModId, out string relativePath)
    {
        relativePath = string.Empty;

        var modYaml = LocateModYaml(instanceDir, engineModId);
        if (modYaml is null)
            return false;

        string[] lines;
        try { lines = File.ReadAllLines(modYaml); }
        catch { return false; }

        const string supportToken = "^SupportDir|";
        var inMapFolders = false;
        foreach (var raw in lines)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            // A line that isn't indented starts a new top-level key, which ends the MapFolders block.
            var indented = raw[0] == ' ' || raw[0] == '\t';
            if (!indented)
            {
                inMapFolders = raw.StartsWith("MapFolders:", StringComparison.Ordinal);
                continue;
            }

            if (!inMapFolders)
                continue;

            // A MapFolders child is "<folder>: <classification>". The folder key holds no colon, so the
            // last ':' separates it from the classification.
            var line = raw.Trim();
            var sep = line.LastIndexOf(':');
            if (sep < 0)
                continue;

            if (!string.Equals(line[(sep + 1)..].Trim(), "User", StringComparison.Ordinal))
                continue;

            var key = line[..sep].Trim();
            if (key.StartsWith('~'))
                key = key[1..].Trim();

            // We can only carry forward folders that live under the support dir we own.
            if (!key.StartsWith(supportToken, StringComparison.Ordinal))
                return false;

            relativePath = key[supportToken.Length..].Trim();
            return relativePath.Length > 0;
        }

        return false;
    }

    static string? LocateModYaml(string instanceDir, string engineModId)
    {
        // The winportable layout puts it at <instance>/mods/<modId>/mod.yaml.
        var direct = Path.Combine(instanceDir, "mods", engineModId, "mod.yaml");
        if (File.Exists(direct))
            return direct;

        // Fall back to a search for a nested layout, matching the mod-id folder so a bundle's other
        // mods' manifests aren't picked up.
        try
        {
            foreach (var path in Directory.EnumerateFiles(instanceDir, "mod.yaml", SearchOption.AllDirectories))
                if (string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), engineModId, StringComparison.OrdinalIgnoreCase))
                    return path;
        }
        catch { /* unreadable tree: treat as no manifest */ }

        return null;
    }
}
