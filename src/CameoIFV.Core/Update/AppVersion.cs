using System.Reflection;
using System.Text.RegularExpressions;

namespace CameoIFV.Core.Update;

/// <summary>
/// The launcher's own version, used to decide whether a newer release is available. Read from the
/// build-injected <see cref="AssemblyInformationalVersionAttribute"/> (the release tag, e.g. "v2.0.0"),
/// tolerating a leading 'v' and the SDK's "+{commit}" SourceLink suffix; falls back to the numeric
/// assembly version. A local/dev build with no injected version parses to whatever the default
/// assembly version is, so callers gate auto-checking on build configuration rather than on this.
/// </summary>
public static class AppVersion
{
    private static readonly Regex SemVerCore = new(@"(\d+)\.(\d+)\.(\d+)", RegexOptions.Compiled);

    /// <summary>Raw version string as built (for display), e.g. "v2.0.0" or "1.0.0.0".</summary>
    public static string Raw { get; } = ReadRaw();

    /// <summary>Parsed Major.Minor.Patch of the running launcher, or null when unparseable.</summary>
    public static Version? Current { get; } = Parse(Raw);

    /// <summary>
    /// True only for a packaged release build. The release workflow injects a clean tag version
    /// ("vX.Y.Z") and disables the SourceLink "+{commit}" suffix; a local/dev build instead reads as
    /// "1.0.0+{commit}" (or a bare assembly version). Self-update should be offered only for releases —
    /// otherwise a dev build offers to "update" itself down to the latest release.
    /// </summary>
    public static bool IsRelease { get; } =
        !string.IsNullOrWhiteSpace(Raw)
        && Raw.StartsWith("v", StringComparison.OrdinalIgnoreCase)
        && !Raw.Contains('+');

    /// <summary>
    /// Extracts a comparable Major.Minor.Patch from a version/tag string, ignoring a leading 'v',
    /// any pre-release/build suffix ("-rc1", "+abc123"), and surrounding noise. Null if none found.
    /// </summary>
    public static Version? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = SemVerCore.Match(text);
        if (!match.Success)
            return null;

        return new Version(
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            int.Parse(match.Groups[3].Value));
    }

    private static string ReadRaw()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
            return info;

        return asm.GetName().Version?.ToString() ?? "0.0.0";
    }
}
