using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CameoIFV.App.Services;
using CameoIFV.Core.Github;
using CameoIFV.Core.Install;
using CameoIFV.Core.Interaction;
using CameoIFV.Core.Model;
using CameoIFV.Core.Update;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CameoIFV.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private readonly LauncherServices _services;

    public ObservableCollection<ModDefinition> Mods { get; } = new();
    public ObservableCollection<ChannelOption> Channels { get; } = new();
    public ObservableCollection<ResolvedRelease> AvailableReleases { get; } = new();
    public ObservableCollection<InstalledInstance> InstalledInstances { get; } = new();
    public ObservableCollection<string> LibraryRoots => _services.LibraryRoots;

    [ObservableProperty] private ModDefinition? _selectedMod;
    [ObservableProperty] private ChannelOption? _selectedChannel;
    [ObservableProperty] private ResolvedRelease? _selectedRelease;
    [ObservableProperty] private InstalledInstance? _selectedInstance;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private string _sessionLog = string.Empty;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _deleteButtonText = "Delete";
    [ObservableProperty] private string _removeLibraryButtonText = "Remove path";
    [ObservableProperty] private string? _selectedLibraryRoot;
    [ObservableProperty] private bool _isLauncherUpdateAvailable;
    [ObservableProperty] private string _launcherUpdateBanner = string.Empty;
    [ObservableProperty] private bool _singleInstanceMode;
    private LauncherUpdate? _pendingLauncherUpdate;

    private readonly ConfirmationGate _deleteConfirmation = new(TimeSpan.FromSeconds(5));
    private readonly ConfirmationGate _removeLibraryConfirmation = new(TimeSpan.FromSeconds(5));
    private bool _suppressLibrarySelectionSwitch;
    private bool _suppressSingleInstanceSave;
    private CancellationTokenSource? _installCancellation;

    public Func<string?, Task<string?>>? PickLibraryFolderAsync { get; set; }

    /// <summary>Set by the view: launch the given executable and shut this instance down.</summary>
    public Action<string>? RequestRelaunch { get; set; }

    public MainWindowViewModel() : this(new LauncherServices()) { }

    public MainWindowViewModel(LauncherServices services)
    {
        _services = services;
        _selectedLibraryRoot = _services.LibraryRoot;
        foreach (var mod in _services.Catalog.Mods.OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            Mods.Add(mod);

        if (_services.LastCleanupResult.TotalDeleted > 0)
        {
            Status = $"Cleaned {_services.LastCleanupResult.TotalDeleted} interrupted install item(s).";
            AppendSessionLog(Status);
        }

        AppendSessionLog($"Session started. Library root: {_services.LibraryRoot}");

        SelectedMod = FindPreferredMod() ?? Mods.FirstOrDefault();
    }

    partial void OnSelectedModChanged(ModDefinition? value)
    {
        Channels.Clear();
        if (value is not null)
        {
            foreach (var source in value.Sources)
                Channels.Add(new ChannelOption(source));
        }

        // Reflect this mod's "update in place" setting without re-saving it back.
        _suppressSingleInstanceSave = true;
        try { SingleInstanceMode = value is not null && _services.IsSingleInstance(value.Id); }
        finally { _suppressSingleInstanceSave = false; }

        RefreshInstalled();
        // Setting SelectedChannel triggers OnSelectedChannelChanged, which lists that feed.
        SelectedChannel = FindPreferredChannel(value) ?? Channels.FirstOrDefault();
        NotifyCommandStatesChanged();
    }

    partial void OnSingleInstanceModeChanged(bool value)
    {
        if (_suppressSingleInstanceSave || SelectedMod is null)
            return;

        var mod = SelectedMod;
        _services.SetSingleInstance(mod.Id, value);
        // The toggle renames the existing install (to/from "main"); reflect it in the list now.
        RefreshInstalled();

        string message;
        if (value)
        {
            // After promotion the latest install is "main"; any remaining version folders will be
            // pruned on the next successful update. Tell the user exactly what to expect.
            var others = InstalledInstances.Count(i =>
                !string.Equals(i.Tag, InstallOrchestrator.SingleInstanceFolder, StringComparison.OrdinalIgnoreCase));

            message = others > 0
                ? $"{mod.DisplayName}: \"Update in place\" is ON. The latest version is now the single \"main\" install. "
                  + $"{others} older version folder(s) are kept for now and will be deleted on the next update to reclaim space."
                : $"{mod.DisplayName}: \"Update in place\" is ON. This install is the single \"main\" folder, "
                  + "overwritten on each update — the game's path (and your shortcut) stays the same.";
        }
        else
        {
            message = $"{mod.DisplayName}: \"Update in place\" is OFF. The current install is restored under its own "
                + "version name, and each future update installs to its own folder (nothing is auto-deleted).";
        }

        Status = message;
        AppendSessionLog(message);
    }

    partial void OnSelectedChannelChanged(ChannelOption? value)
    {
        if (SelectedMod is not null && value is not null)
            _services.SetSelectedFeed(SelectedMod, value.Source);

        NotifyCommandStatesChanged();
        _ = RefreshReleasesAsync();
    }

    partial void OnSelectedReleaseChanged(ResolvedRelease? value)
    {
        InstallCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedInstanceChanged(InstalledInstance? value)
    {
        ClearPendingDelete();
        PlayCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsBusyChanged(bool value)
    {
        NotifyCommandStatesChanged();
    }

    partial void OnSelectedLibraryRootChanged(string? value)
    {
        ClearPendingRemoveLibrary();
        if (_suppressLibrarySelectionSwitch)
            return;

        if (!string.IsNullOrWhiteSpace(value) && !StringComparer.OrdinalIgnoreCase.Equals(value, _services.LibraryRoot))
            SwitchLibraryRoot(value);
        RemoveLibraryCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRefreshReleases))]
    private async Task RefreshReleasesAsync()
    {
        if (SelectedMod is null || SelectedChannel is null || IsBusy)
            return;

        IsBusy = true;
        Status = $"Checking {SelectedChannel.Label}…";
        AppendSessionLog($"Refreshing releases for {SelectedMod.DisplayName} / {SelectedChannel.Label}.");
        try
        {
            AvailableReleases.Clear();
            // Show exactly the selected feed (e.g. CA dev = darkademic/CAmod), correctly labelled.
            var releases = await _services.ReleaseProvider.ListAsync(SelectedChannel.Source, _services.Platform, CancellationToken.None);
            foreach (var r in releases)
                AvailableReleases.Add(r);

            SelectedRelease = AvailableReleases.FirstOrDefault();
            Status = AvailableReleases.Count > 0
                ? $"{AvailableReleases.Count} release(s) available."
                : "No releases found.";
            AppendSessionLog(Status);
        }
        catch (Exception ex)
        {
            Status = $"Failed to list releases: {ex.Message}";
            AppendSessionLog(Status);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRefreshReleases() => SelectedMod is not null && SelectedChannel is not null && !IsBusy;

    private ModDefinition? FindPreferredMod()
    {
        if (string.IsNullOrWhiteSpace(_services.SelectedModId))
            return null;

        return Mods.FirstOrDefault(mod => string.Equals(mod.Id, _services.SelectedModId, StringComparison.OrdinalIgnoreCase));
    }

    private ChannelOption? FindPreferredChannel(ModDefinition? mod)
    {
        if (mod is null)
            return null;

        // Each mod remembers its own channel, so switching away and back restores it.
        var saved = _services.ChannelFor(mod.Id);
        if (saved is null)
            return null;

        var exactMatch = Channels.FirstOrDefault(channel =>
            channel.Source.Channel == saved.Channel &&
            string.Equals(channel.Source.Repository, saved.Repository, StringComparison.OrdinalIgnoreCase));

        return exactMatch ?? Channels.FirstOrDefault(channel => channel.Source.Channel == saved.Channel);
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (SelectedMod is null || SelectedRelease is null || IsBusy)
            return;

        var mod = SelectedMod;
        var release = SelectedRelease;

        ClearPendingDelete();
        AppendSessionLog($"""
        Install requested.
        Mod: {mod.DisplayName}
        Version: {release.TagName}
        Channel: {release.Channel}
        Asset: {release.AssetName}
        Download URL: {release.AssetUrl}
        Zsync URL: {release.ZsyncUrl?.ToString() ?? "(none)"}
        """);
        IsBusy = true;
        Progress = 0;
        _installCancellation = new CancellationTokenSource();
        CancelInstallCommand.NotifyCanExecuteChanged();
        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                Progress = p.Fraction * 100;
                Status = FormatInstallStatus(release.TagName, p);
                if (!string.IsNullOrWhiteSpace(p.Detail))
                    AppendSessionLog(p.Detail);
            });

            var token = _installCancellation.Token;
            var singleInstance = _services.IsSingleInstance(mod.Id);
            var result = await Task.Run(
                () => _services.Installer.InstallAsync(mod, release, progress, token, singleInstance),
                token);
            RefreshInstalled();
            // Reselect by directory, not tag: in single-instance mode the folder is "main", not the tag.
            SelectedInstance = InstalledInstances.FirstOrDefault(
                i => string.Equals(i.Dir, result.InstanceDir, StringComparison.OrdinalIgnoreCase))
                ?? InstalledInstances.FirstOrDefault();
            Status = result.ExecutablePath is null
                ? $"Installed {release.TagName} (no executable found)."
                : $"Installed {release.TagName}.";
            AppendSessionLog(Status);
        }
        catch (OperationCanceledException)
        {
            Status = $"Install canceled for {release.TagName}.";
            AppendSessionLog(Status);
        }
        catch (Exception ex)
        {
            Status = $"Install failed: {ex.Message}";
            AppendSessionLog(Status);
        }
        finally
        {
            _installCancellation?.Dispose();
            _installCancellation = null;
            Progress = 0;
            IsBusy = false;
            CancelInstallCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanInstall() => SelectedMod is not null && SelectedRelease is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanCancelInstall))]
    private void CancelInstall()
    {
        _installCancellation?.Cancel();
        Status = "Canceling install...";
        AppendSessionLog(Status);
        CancelInstallCommand.NotifyCanExecuteChanged();
    }

    private bool CanCancelInstall() => IsBusy && _installCancellation is { IsCancellationRequested: false };

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play()
    {
        if (SelectedInstance is null || SelectedMod is null)
            return;

        ClearPendingDelete();
        AppendSessionLog(FormatPlayLog(SelectedMod, SelectedInstance));
        try
        {
            _services.Instances.Launch(SelectedInstance, SelectedMod);
            Status = $"Launched {SelectedInstance.DisplayVersion}.";
            AppendSessionLog(Status);
        }
        catch (Exception ex)
        {
            Status = $"Launch failed: {ex.Message}";
            AppendSessionLog(Status);
        }
    }

    private bool CanPlay() => SelectedInstance?.IsRunnable == true && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenDataFolder() => OpenProjectFolder(ProjectFolderKind.Data, "data");

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenReplaysFolder() => OpenProjectFolder(ProjectFolderKind.Replays, "replays");

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenMapsFolder() => OpenProjectFolder(ProjectFolderKind.Maps, "maps");

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenSavesFolder() => OpenProjectFolder(ProjectFolderKind.Saves, "saves");

    private bool CanOpenFolder() => SelectedMod is not null;

    private void OpenProjectFolder(ProjectFolderKind kind, string label)
    {
        if (SelectedMod is null)
            return;

        // Replays/Maps/Saves live under {kind}/{mod}/{version}; deep-link to the selected install's
        // version when one is selected. Data is not version-namespaced, and no install means there's
        // no version to target, so both fall back to the per-mod parent folder.
        var version = kind == ProjectFolderKind.Data ? null : SelectedInstance?.DisplayVersion;

        try
        {
            var path = _services.OpenProjectFolder(SelectedMod, kind, version);
            Status = $"Opened {label} folder.";
            AppendSessionLog($"Opened {label} folder: {path}");
        }
        catch (Exception ex)
        {
            Status = $"Could not open {label} folder: {ex.Message}";
            AppendSessionLog(Status);
        }
    }

    // Launcher updates are fully manual: this runs only when the user clicks "Check for launcher
    // updates". There is no automatic startup check, so the launcher never reaches out on its own.
    [RelayCommand(CanExecute = nameof(CanCheckForLauncherUpdate))]
    private async Task CheckForLauncherUpdate()
    {
        try
        {
            Status = "Checking for launcher updates…";

            var update = await _services.CheckForLauncherUpdateAsync(includePrerelease: false, CancellationToken.None);
            if (update is null)
            {
                _pendingLauncherUpdate = null;
                IsLauncherUpdateAvailable = false;
                ApplyLauncherUpdateCommand.NotifyCanExecuteChanged();
                Status = $"Cameo-IFV is up to date ({_services.CurrentVersionDisplay}).";
                AppendSessionLog(Status);
                return;
            }

            _pendingLauncherUpdate = update;
            LauncherUpdateBanner = $"Cameo-IFV {update.DisplayVersion} is available — you have {_services.CurrentVersionDisplay}.";
            IsLauncherUpdateAvailable = true;
            ApplyLauncherUpdateCommand.NotifyCanExecuteChanged();
            Status = LauncherUpdateBanner;
            AppendSessionLog(Status);
        }
        catch (Exception ex)
        {
            Status = $"Update check failed: {ex.Message}";
            AppendSessionLog(Status);
        }
    }

    private bool CanCheckForLauncherUpdate() => !IsBusy;

    [RelayCommand]
    private void DismissLauncherUpdate() => IsLauncherUpdateAvailable = false;

    [RelayCommand(CanExecute = nameof(CanApplyLauncherUpdate))]
    private async Task ApplyLauncherUpdate()
    {
        if (_pendingLauncherUpdate is null || IsBusy)
            return;

        var update = _pendingLauncherUpdate;
        IsBusy = true;
        Progress = 0;
        Status = $"Downloading Cameo-IFV {update.DisplayVersion}…";
        AppendSessionLog(Status);
        try
        {
            var progress = new Progress<UpdateProgress>(p =>
            {
                Progress = p.Fraction * 100;
                if (!string.IsNullOrWhiteSpace(p.Message))
                    AppendSessionLog(p.Message);
            });

            var newExe = await Task.Run(
                () => _services.ApplyLauncherUpdateAsync(update, progress, CancellationToken.None));

            IsLauncherUpdateAvailable = false;
            Status = $"Updated to {update.DisplayVersion}. Restarting…";
            AppendSessionLog($"Launcher updated to {update.DisplayVersion}. Relaunching {newExe}.");

            if (RequestRelaunch is not null)
                RequestRelaunch(newExe);
            else
                Status = $"Update installed. Restart Cameo-IFV to use {update.DisplayVersion}.";
        }
        catch (Exception ex)
        {
            Status = $"Launcher update failed: {ex.Message}";
            AppendSessionLog(Status);
        }
        finally
        {
            IsBusy = false;
            Progress = 0;
        }
    }

    private bool CanApplyLauncherUpdate() => _pendingLauncherUpdate is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (SelectedInstance is null || IsBusy)
            return;

        var now = DateTimeOffset.UtcNow;
        if (!_deleteConfirmation.Confirm(SelectedInstance.Tag, now))
        {
            var version = _deleteConfirmation.Arm(SelectedInstance.Tag, now);
            DeleteButtonText = "Confirm Delete";
            Status = $"Click Confirm Delete within 5 seconds to remove {SelectedInstance.DisplayVersion}.";
            ExpireDeleteConfirmationAfterDelay(SelectedInstance.Tag, version);
            return;
        }

        try
        {
            _services.Instances.Delete(SelectedInstance);
            Status = $"Deleted {SelectedInstance.DisplayVersion}.";
            AppendSessionLog(Status);
            ClearPendingDelete();
            RefreshInstalled();
        }
        catch (Exception ex)
        {
            Status = $"Delete failed: {ex.Message}";
            AppendSessionLog(Status);
        }
    }

    private bool CanDelete() => SelectedInstance is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAddLibrary))]
    private async Task AddLibraryAsync()
    {
        if (PickLibraryFolderAsync is null)
        {
            Status = "Folder picker is not available.";
            AppendSessionLog(Status);
            return;
        }

        var picked = await PickLibraryFolderAsync(SelectedLibraryRoot);
        if (!string.IsNullOrWhiteSpace(picked))
            SwitchLibraryRoot(picked);
    }

    private bool CanAddLibrary() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanRemoveLibrary))]
    private void RemoveLibrary()
    {
        if (string.IsNullOrWhiteSpace(SelectedLibraryRoot) || IsBusy)
            return;

        var now = DateTimeOffset.UtcNow;
        if (!_removeLibraryConfirmation.Confirm(SelectedLibraryRoot, now))
        {
            var version = _removeLibraryConfirmation.Arm(SelectedLibraryRoot, now);
            RemoveLibraryButtonText = "Confirm Remove";
            Status = $"Click Confirm Remove within 5 seconds to forget {SelectedLibraryRoot}. Files will not be deleted.";
            ExpireRemoveLibraryConfirmationAfterDelay(SelectedLibraryRoot, version);
            return;
        }

        try
        {
            var removed = SelectedLibraryRoot;
            var wasActive = StringComparer.OrdinalIgnoreCase.Equals(removed, _services.LibraryRoot);
            _services.RemoveLibraryRoot(removed);
            _suppressLibrarySelectionSwitch = true;
            try
            {
                SelectedLibraryRoot = _services.LibraryRoot;
            }
            finally
            {
                _suppressLibrarySelectionSwitch = false;
            }
            RefreshInstalled();
            ClearPendingRemoveLibrary();
            NotifyCommandStatesChanged();
            Status = wasActive
                ? $"Forgot {removed}. Active library switched to {_services.LibraryRoot}. Files were not deleted."
                : $"Forgot {removed}. Files were not deleted.";
            AppendSessionLog(Status);
        }
        catch (Exception ex)
        {
            Status = $"Failed to remove library path: {ex.Message}";
            AppendSessionLog(Status);
        }
    }

    private bool CanRemoveLibrary() => !IsBusy && !string.IsNullOrWhiteSpace(SelectedLibraryRoot);

    private void SwitchLibraryRoot(string libraryRoot)
    {
        libraryRoot = libraryRoot.Trim();
        if (string.IsNullOrWhiteSpace(libraryRoot))
            return;

        try
        {
            ClearPendingDelete();
            var previousRoot = _services.LibraryRoot;
            _services.SetLibraryRoot(libraryRoot);
            // Re-assert the active root as the dropdown selection without re-triggering a switch,
            // so the ComboBox shows the current library instead of going blank after the list updates.
            _suppressLibrarySelectionSwitch = true;
            try
            {
                SelectedLibraryRoot = _services.LibraryRoot;
            }
            finally
            {
                _suppressLibrarySelectionSwitch = false;
            }
            RefreshInstalled();
            NotifyCommandStatesChanged();
            Status = $"Library location set to {_services.LibraryRoot}. Previous installs remain in {previousRoot}; move or reinstall them if needed.";
            AppendSessionLog($"""
            Library location changed.
            Previous library: {previousRoot}
            Active library: {_services.LibraryRoot}
            Previous installs remain in the previous library unless moved or reinstalled.
            """);
        }
        catch (Exception ex)
        {
            Status = $"Failed to set library location: {ex.Message}";
            AppendSessionLog(Status);
        }
    }

    private void RefreshInstalled()
    {
        InstalledInstances.Clear();
        if (SelectedMod is null)
            return;

        foreach (var instance in _services.Instances.ListInstalled(SelectedMod))
            InstalledInstances.Add(instance);

        SelectedInstance = InstalledInstances.FirstOrDefault();
        PlayCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    private void ClearPendingDelete()
    {
        _deleteConfirmation.Clear();
        DeleteButtonText = "Delete";
    }

    private async void ExpireDeleteConfirmationAfterDelay(string tag, int version)
    {
        await Task.Delay(_deleteConfirmation.Window);
        if (!_deleteConfirmation.IsExpired(tag, DateTimeOffset.UtcNow, version))
            return;

        ClearPendingDelete();
        Status = $"Delete confirmation expired for {tag}.";
        AppendSessionLog(Status);
    }

    private void ClearPendingRemoveLibrary()
    {
        _removeLibraryConfirmation.Clear();
        RemoveLibraryButtonText = "Remove path";
    }

    private async void ExpireRemoveLibraryConfirmationAfterDelay(string libraryRoot, int version)
    {
        await Task.Delay(_removeLibraryConfirmation.Window);
        if (!_removeLibraryConfirmation.IsExpired(libraryRoot, DateTimeOffset.UtcNow, version))
            return;

        ClearPendingRemoveLibrary();
        Status = $"Remove path confirmation expired for {libraryRoot}.";
        AppendSessionLog(Status);
    }

    private static string FormatInstallStatus(string tagName, InstallProgress progress)
    {
        var mode = FormatUpdateMode(progress.UpdateMode);
        if (progress.Phase != InstallPhase.Downloading)
            return $"{progress.Phase}: {tagName} ({mode})...";

        if (progress.TotalBytes <= 0)
            return $"Downloading: {tagName} ({mode})...";

        var transferred = FormatBytes(progress.BytesTransferred);
        var total = FormatBytes(progress.TotalBytes);
        var verb = progress.UpdateMode == UpdateMode.IncrementalZsync ? "fetched" : "downloaded";
        return $"Downloading: {tagName} ({mode}, {verb} {transferred} of {total}, {progress.Fraction * 100:F0}%)";
    }

    private static string FormatUpdateMode(UpdateMode mode) => mode switch
    {
        UpdateMode.IncrementalZsync => "incremental zsync",
        _ => "full download",
    };

    private static string FormatBytes(long bytes)
    {
        const double kib = 1024;
        const double mib = kib * 1024;
        const double gib = mib * 1024;

        return bytes switch
        {
            >= (long)gib => $"{bytes / gib:F2} GB",
            >= (long)mib => $"{bytes / mib:F1} MB",
            >= (long)kib => $"{bytes / kib:F0} KB",
            _ => $"{bytes} B",
        };
    }

    private void AppendSessionLog(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        var entry = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {message.Trim()}";
        SessionLog = string.IsNullOrEmpty(SessionLog)
            ? entry
            : $"{SessionLog}{Environment.NewLine}{Environment.NewLine}{entry}";
    }

    private static string FormatPlayLog(ModDefinition? mod, InstalledInstance instance)
    {
        var metadata = instance.Metadata;
        return $"""
        Starting game...
        Mod: {metadata?.ModDisplayName ?? mod?.DisplayName ?? instance.ModId}
        Version: {metadata?.Tag ?? instance.Tag}
        Channel: {metadata?.Channel?.ToString() ?? "(unknown)"}
        Downloaded from: {metadata?.AssetUrl ?? "(unknown)"}
        Executable path: {instance.ExecutablePath ?? "(none)"}
        Instance directory: {instance.Dir}
        """;
    }

    private void NotifyCommandStatesChanged()
    {
        RefreshReleasesCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
        PlayCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        AddLibraryCommand.NotifyCanExecuteChanged();
        RemoveLibraryCommand.NotifyCanExecuteChanged();
        CancelInstallCommand.NotifyCanExecuteChanged();
        OpenDataFolderCommand.NotifyCanExecuteChanged();
        OpenReplaysFolderCommand.NotifyCanExecuteChanged();
        OpenMapsFolderCommand.NotifyCanExecuteChanged();
        OpenSavesFolderCommand.NotifyCanExecuteChanged();
        CheckForLauncherUpdateCommand.NotifyCanExecuteChanged();
        ApplyLauncherUpdateCommand.NotifyCanExecuteChanged();
    }
}
