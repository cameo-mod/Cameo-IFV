using System.Diagnostics;
using System.Text.Json;
using CameoIFV.Core.Model;
using CameoIFV.Core.Storage;

namespace CameoIFV.Core.Install;

public sealed record InstalledInstance(string ModId, string Tag, string Dir, string? ExecutablePath, InstallMetadata? Metadata = null)
{
    public bool IsRunnable => ExecutablePath is not null && File.Exists(ExecutablePath);
}

/// <summary>
/// Enumerates and launches installed instances on disk. Independent of the GitHub/update layer so
/// the launcher can show what's installed even fully offline.
/// </summary>
public sealed class InstanceManager
{
    private readonly LauncherPaths _paths;

    public InstanceManager(LauncherPaths paths) => _paths = paths;

    public IReadOnlyList<InstalledInstance> ListInstalled(ModDefinition mod)
    {
        var modRoot = Path.Combine(_paths.Root, "instances", mod.Id);
        if (!Directory.Exists(modRoot))
            return Array.Empty<InstalledInstance>();

        var result = new List<InstalledInstance>();
        foreach (var dir in Directory.EnumerateDirectories(modRoot))
        {
            var exe = ExecutableLocator.Locate(dir, mod.LaunchExecutable);
            result.Add(new InstalledInstance(mod.Id, Path.GetFileName(dir), dir, exe, ReadMetadata(dir)));
        }

        // Newest install first (folder write time is a good proxy for install order).
        return result
            .OrderByDescending(i => Directory.GetLastWriteTimeUtc(i.Dir))
            .ToList();
    }

    public void Delete(InstalledInstance instance)
    {
        if (Directory.Exists(instance.Dir))
            Directory.Delete(instance.Dir, recursive: true);
    }

    public Process Launch(InstalledInstance instance)
    {
        if (!instance.IsRunnable)
            throw new InvalidOperationException($"Instance {instance.Tag} has no runnable executable.");

        var startInfo = new ProcessStartInfo
        {
            FileName = instance.ExecutablePath!,
            WorkingDirectory = Path.GetDirectoryName(instance.ExecutablePath!),
            UseShellExecute = true,
        };

        return Process.Start(startInfo)
               ?? throw new InvalidOperationException($"Failed to start {instance.ExecutablePath}.");
    }

    private static InstallMetadata? ReadMetadata(string instanceDir)
    {
        var path = Path.Combine(instanceDir, InstallOrchestrator.MetadataFileName);
        if (!File.Exists(path))
            return null;

        try
        {
            return JsonSerializer.Deserialize<InstallMetadata>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }
}
