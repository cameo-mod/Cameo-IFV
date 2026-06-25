using System.Diagnostics;
using System.Text.Json;
using CameoIFV.Core.Model;
using CameoIFV.Core.Storage;

namespace CameoIFV.Core.Install;

public sealed record InstalledInstance(string ModId, string Tag, string Dir, string? ExecutablePath, InstallMetadata? Metadata = null)
{
    public bool IsRunnable => ExecutablePath is not null && File.Exists(ExecutablePath);

    /// <summary>
    /// The label to show the user. <see cref="Tag"/> is the on-disk folder name; in "update in place"
    /// mode that's the fixed "main", so show <c>Main (real-version)</c> — surfacing both the mode and
    /// the actual installed version. A normal per-version install just shows its version.
    /// </summary>
    public string DisplayVersion
    {
        get
        {
            var version = Metadata?.Tag;
            if (string.Equals(Tag, InstallOrchestrator.SingleInstanceFolder, StringComparison.OrdinalIgnoreCase))
                return string.IsNullOrWhiteSpace(version) ? "Main" : $"Main ({version})";

            return version ?? Tag;
        }
    }
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

    /// <summary>
    /// Adopts this mod's latest installed instance as the fixed "main" folder when the user turns on
    /// "update in place", so the switch takes effect on the existing install immediately rather than
    /// only on the next update — letting a desktop shortcut point at the stable path right away. The
    /// rename is reversible (see <see cref="DemoteFromSingleInstance"/>): the real version stays in the
    /// instance metadata, surfaced via <see cref="InstalledInstance.DisplayVersion"/>. Best-effort — a
    /// running/locked instance just stays under its current name.
    /// </summary>
    public void PromoteToSingleInstance(ModDefinition mod)
    {
        try
        {
            var latest = ListInstalled(mod).FirstOrDefault();
            if (latest is null)
                return;

            var mainDir = _paths.InstanceDir(mod.Id, InstallOrchestrator.SingleInstanceFolder);
            if (PathsEqual(latest.Dir, mainDir))
                return; // the latest install is already "main"

            if (Directory.Exists(mainDir))
            {
                // An older "main" is superseded by the newer latest; swap it out with a rollback.
                var backup = mainDir + ".old-" + Guid.NewGuid().ToString("N");
                Directory.Move(mainDir, backup);
                try { Directory.Move(latest.Dir, mainDir); }
                catch { Directory.Move(backup, mainDir); throw; }
                TryDeleteDir(backup);
            }
            else
            {
                Directory.Move(latest.Dir, mainDir);
            }
        }
        catch { /* best effort: leave the instance under its current name on any failure */ }
    }

    /// <summary>
    /// Reverses <see cref="PromoteToSingleInstance"/> when "update in place" is turned back off:
    /// renames the "main" folder back to its real version (from the metadata) so the list shows the
    /// version again and no stale "main" lingers. Skipped when the version is unknown or a folder for
    /// that version already exists.
    /// </summary>
    public void DemoteFromSingleInstance(ModDefinition mod)
    {
        try
        {
            var mainDir = _paths.InstanceDir(mod.Id, InstallOrchestrator.SingleInstanceFolder);
            if (!Directory.Exists(mainDir))
                return;

            var version = ReadMetadata(mainDir)?.Tag;
            if (string.IsNullOrWhiteSpace(version))
                return;

            var versionDir = _paths.InstanceDir(mod.Id, version);
            if (PathsEqual(versionDir, mainDir) || Directory.Exists(versionDir))
                return;

            Directory.Move(mainDir, versionDir);
        }
        catch { /* best effort: leave it as "main" on any failure */ }
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(
            Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best effort */ }
    }

    public Process Launch(InstalledInstance instance, ModDefinition mod)
    {
        if (!instance.IsRunnable)
            throw new InvalidOperationException($"Instance {instance.Tag} has no runnable executable.");

        // Isolate this project's user data in its own support dir (shared across the project's
        // versions), seeding it from the shared %AppData%\OpenRA on first launch.
        var engineModId = mod.EngineModId ?? mod.Id;
        var supportDir = SupportDirManager.Prepare(_paths.SupportDir(mod.Id), engineModId);

        // Carry the player's custom maps forward from the version they last played: OpenRA reads maps
        // from a per-version folder, so without this an update hides them until moved by hand.
        if (ModManifestReader.TryGetUserMapFolder(instance.Dir, engineModId, out var userMapFolder))
            MapMigrator.CarryForward(supportDir, userMapFolder);

        // UseShellExecute must be false so ArgumentList drives deterministic command-line quoting.
        var startInfo = new ProcessStartInfo
        {
            FileName = instance.ExecutablePath!,
            WorkingDirectory = Path.GetDirectoryName(instance.ExecutablePath!),
            UseShellExecute = false,
        };

        // Point the engine at the per-project support dir. The value is wrapped in literal quotes so
        // it survives the branded launcher's naive argument re-join (string.Join(" ")) and any spaces
        // in the path (e.g. a Windows username with a space). The trailing separator is stripped so a
        // path-ending backslash can't escape the closing quote.
        var supportArg = supportDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        startInfo.ArgumentList.Add($"Engine.SupportDir=\"{supportArg}\"");

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
