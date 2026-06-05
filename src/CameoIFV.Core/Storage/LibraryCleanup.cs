namespace CameoIFV.Core.Storage;

public sealed record LibraryCleanupResult(
    int PartialDownloadsDeleted,
    int StagingDirectoriesDeleted,
    int BackupDirectoriesDeleted)
{
    public int TotalDeleted => PartialDownloadsDeleted + StagingDirectoriesDeleted + BackupDirectoriesDeleted;
}

public static class LibraryCleanup
{
    public static LibraryCleanupResult CleanInterruptedInstalls(LauncherPaths paths)
    {
        paths.EnsureBaseDirs();

        var partials = DeleteFiles(paths.DownloadsDir, "*.part");
        var staging = DeleteDirectories(paths.InstancesDir, "*.staging-*");
        var backups = DeleteDirectories(paths.InstancesDir, "*.backup-*");

        return new LibraryCleanupResult(partials, staging, backups);
    }

    private static int DeleteFiles(string root, string pattern)
    {
        if (!Directory.Exists(root))
            return 0;

        var deleted = 0;
        foreach (var file in Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch
            {
                // Best effort: a still-running process or antivirus scanner may briefly hold the file.
            }
        }

        return deleted;
    }

    private static int DeleteDirectories(string root, string pattern)
    {
        if (!Directory.Exists(root))
            return 0;

        var deleted = 0;
        foreach (var modDir in Directory.EnumerateDirectories(root))
        {
            foreach (var dir in Directory.EnumerateDirectories(modDir, pattern, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                    deleted++;
                }
                catch
                {
                    // Best effort: leave anything locked for the next startup.
                }
            }
        }

        return deleted;
    }
}
