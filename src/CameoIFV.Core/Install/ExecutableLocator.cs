namespace CameoIFV.Core.Install;

/// <summary>
/// Finds the runnable executable inside an extracted instance. Prefers the mod's configured
/// <c>LaunchExecutable</c>; otherwise falls back to a single discoverable top-level .exe.
/// </summary>
public static class ExecutableLocator
{
    public static string? Locate(string instanceDir, string? configuredName)
    {
        if (!Directory.Exists(instanceDir))
            return null;

        if (!string.IsNullOrEmpty(configuredName))
        {
            // Search top-level first (the common winportable layout), then anywhere.
            var top = Path.Combine(instanceDir, configuredName);
            if (File.Exists(top))
                return top;

            var nested = Directory.EnumerateFiles(instanceDir, configuredName, SearchOption.AllDirectories).FirstOrDefault();
            if (nested is not null)
                return nested;
        }

        // Fallback: a single .exe at the top level is unambiguous; more than one is not, so give up.
        var topExes = Directory.EnumerateFiles(instanceDir, "*.exe", SearchOption.TopDirectoryOnly).ToList();
        return topExes.Count == 1 ? topExes[0] : null;
    }
}
