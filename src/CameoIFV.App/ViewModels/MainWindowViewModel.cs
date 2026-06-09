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

    private readonly ConfirmationGate _deleteConfirmation = new(TimeSpan.FromSeconds(5));
    private readonly ConfirmationGate _removeLibraryConfirmation = new(TimeSpan.FromSeconds(5));
    private bool _suppressLibrarySelectionSwitch;
    private CancellationTokenSource? _installCancellation;

    public Func<string?, Task<string?>>? PickLibraryFolderAsync { get; set; }

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

        RefreshInstalled();
        // Setting SelectedChannel triggers OnSelectedChannelChanged, which lists that feed.
        SelectedChannel = FindPreferredChannel(value) ?? Channels.FirstOrDefault();
        NotifyCommandStatesChanged();
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
        var saved = _services.SelectedChannel;
        if (mod is null || saved is null)
            return null;

        if (!string.Equals(saved.ModId, mod.Id, StringComparison.OrdinalIgnoreCase))
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
            var result = await Task.Run(
                () => _services.Installer.InstallAsync(mod, release, progress, token),
                token);
            RefreshInstalled();
            SelectedInstance = InstalledInstances.FirstOrDefault(i => i.Tag == release.TagName);
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
            Status = $"Launched {SelectedInstance.Tag}.";
            AppendSessionLog(Status);
        }
        catch (Exception ex)
        {
            Status = $"Launch failed: {ex.Message}";
            AppendSessionLog(Status);
        }
    }

    private bool CanPlay() => SelectedInstance?.IsRunnable == true && !IsBusy;

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
            Status = $"Click Confirm Delete within 5 seconds to remove {SelectedInstance.Tag}.";
            ExpireDeleteConfirmationAfterDelay(SelectedInstance.Tag, version);
            return;
        }

        try
        {
            _services.Instances.Delete(SelectedInstance);
            Status = $"Deleted {SelectedInstance.Tag}.";
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
    }
}
