using CameoIFV.Core.Model;
using CameoIFV.Core.Storage;

namespace CameoIFV.Core.Install;

/// <summary>The user-data folders a player typically wants to reach from the launcher.</summary>
public enum ProjectFolderKind
{
    /// <summary>The project's isolated support dir root (settings, logs, screenshots, etc.).</summary>
    Data,
    Replays,
    Maps,
    Saves,
}

/// <summary>
/// Resolves the well-known per-project user-data folders so the launcher can open them. The folders
/// live under the project's isolated support dir (see <see cref="LauncherPaths.SupportDir"/>), shared
/// across all installed versions of that project.
/// </summary>
public sealed class ProjectFolders
{
    private readonly LauncherPaths _paths;

    public ProjectFolders(LauncherPaths paths) => _paths = paths;

    /// <summary>
    /// Returns the requested folder, ready to open. Runs the same marker-gated migration a launch
    /// performs first, so the very first open surfaces the player's existing %AppData%\OpenRA data;
    /// then ensures the target folder exists so opening it never fails on a never-launched project.
    ///
    /// Replays/Maps/Saves are namespaced by mod version on disk (<c>{kind}/{modId}/{version}</c>).
    /// When <paramref name="version"/> is given and that version's subfolder already exists, the
    /// deep-linked subfolder is returned; otherwise the per-mod parent is returned so the player
    /// still lands on something (e.g. their migrated history) rather than an empty or missing folder.
    /// </summary>
    public string Resolve(ModDefinition mod, ProjectFolderKind kind, string? version = null, string? sharedSupportDir = null)
    {
        var engineModId = mod.EngineModId ?? mod.Id;

        // Prepare BEFORE creating the leaf folder: the migration only copies a subtree when its
        // destination is absent, so pre-creating it here would otherwise suppress the seed copy.
        var support = SupportDirManager.Prepare(_paths.SupportDir(mod.Id), engineModId, sharedSupportDir);

        var parent = kind switch
        {
            ProjectFolderKind.Replays => Path.Combine(support, "Replays", engineModId),
            ProjectFolderKind.Maps => Path.Combine(support, "maps", engineModId),
            ProjectFolderKind.Saves => Path.Combine(support, "Saves", engineModId),
            _ => support,
        };

        // Deep-link only into a version subfolder that already exists. Never create it here: an
        // absent subfolder means no data for that version yet, so falling back to the parent is more
        // useful than opening an empty folder, and avoids masking a tag-vs-version mismatch.
        if (kind != ProjectFolderKind.Data && !string.IsNullOrWhiteSpace(version))
        {
            var versioned = Path.Combine(parent, version);
            if (Directory.Exists(versioned))
                return versioned;
        }

        Directory.CreateDirectory(parent);
        return parent;
    }
}
