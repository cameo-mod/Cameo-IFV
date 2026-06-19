using System.Diagnostics;

namespace CameoIFV.Core.Interaction;

/// <summary>
/// Reveals a directory in the host's file manager. Kept OS-agnostic like the rest of Core:
/// Explorer on Windows, Finder on macOS, the freedesktop handler on Linux.
/// </summary>
public static class FolderOpener
{
    public static void Open(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Path is empty.", nameof(path));

        var (fileName, argument) = Command(path);

        // UseShellExecute=false with an explicit handler keeps quoting deterministic and avoids the
        // shell-association ambiguity of opening a bare directory path on non-Windows platforms.
        var startInfo = new ProcessStartInfo { FileName = fileName, UseShellExecute = false };
        startInfo.ArgumentList.Add(argument);

        Process.Start(startInfo);
    }

    private static (string FileName, string Argument) Command(string path)
    {
        if (OperatingSystem.IsWindows())
            return ("explorer.exe", path);
        if (OperatingSystem.IsMacOS())
            return ("open", path);

        // Linux and other Unixes: the freedesktop "open this with the default handler" tool.
        return ("xdg-open", path);
    }
}
