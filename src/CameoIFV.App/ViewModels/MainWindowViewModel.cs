using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CameoIFV.App.Services;
using CameoIFV.Core.Github;
using CameoIFV.Core.Install;
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
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _deleteButtonText = "Delete";
    [ObservableProperty] private string _removeLibraryButtonText = "Remove path";
    [ObservableProperty] private string? _selectedLibraryRoot;

    private string? _pendingDeleteTag;
    private int _deleteConfirmationVersion;
    private string? _pendingRemoveLibraryRoot;
    private int _removeLibraryConfirmationVersion;
    private bool _suppressLibrarySelectionSwitch;

    public Func<string?, Task<string?>>? PickLibraryFolderAsync { get; set; }

    public MainWindowViewModel() : this(new LauncherServices()) { }

    public MainWindowViewModel(LauncherServices services)
    {
        _services = services;
        _selectedLibraryRoot = _services.LibraryRoot;
        foreach (var mod in _services.Catalog.Mods)
            Mods.Add(mod);
        SelectedMod = Mods.FirstOrDefault();
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
        SelectedChannel = Channels.FirstOrDefault();
        NotifyCommandStatesChanged();
    }

    partial void OnSelectedChannelChanged(ChannelOption? value)
    {
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
        }
        catch (Exception ex)
        {
            Status = $"Failed to list releases: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRefreshReleases() => SelectedMod is not null && SelectedChannel is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (SelectedMod is null || SelectedRelease is null || IsBusy)
            return;

        var mod = SelectedMod;
        var release = SelectedRelease;

        ClearPendingDelete();
        IsBusy = true;
        Progress = 0;
        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                Progress = p.Fraction * 100;
                Status = FormatInstallStatus(release.TagName, p);
            });

            var result = await Task.Run(
                () => _services.Installer.InstallAsync(mod, release, progress, CancellationToken.None),
                CancellationToken.None);
            RefreshInstalled();
            SelectedInstance = InstalledInstances.FirstOrDefault(i => i.Tag == release.TagName);
            Status = result.ExecutablePath is null
                ? $"Installed {release.TagName} (no executable found)."
                : $"Installed {release.TagName}.";
        }
        catch (Exception ex)
        {
            Status = $"Install failed: {ex.Message}";
        }
        finally
        {
            Progress = 0;
            IsBusy = false;
        }
    }

    private bool CanInstall() => SelectedMod is not null && SelectedRelease is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanPlay))]
    private void Play()
    {
        if (SelectedInstance is null)
            return;

        ClearPendingDelete();
        try
        {
            _services.Instances.Launch(SelectedInstance);
            Status = $"Launched {SelectedInstance.Tag}.";
        }
        catch (Exception ex)
        {
            Status = $"Launch failed: {ex.Message}";
        }
    }

    private bool CanPlay() => SelectedInstance?.IsRunnable == true && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanDelete))]
    private void Delete()
    {
        if (SelectedInstance is null || IsBusy)
            return;

        if (_pendingDeleteTag != SelectedInstance.Tag)
        {
            _pendingDeleteTag = SelectedInstance.Tag;
            DeleteButtonText = "Confirm Delete";
            Status = $"Click Confirm Delete within 5 seconds to remove {SelectedInstance.Tag}.";
            ExpireDeleteConfirmationAfterDelay(SelectedInstance.Tag, ++_deleteConfirmationVersion);
            return;
        }

        try
        {
            _services.Instances.Delete(SelectedInstance);
            Status = $"Deleted {SelectedInstance.Tag}.";
            ClearPendingDelete();
            RefreshInstalled();
        }
        catch (Exception ex)
        {
            Status = $"Delete failed: {ex.Message}";
        }
    }

    private bool CanDelete() => SelectedInstance is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAddLibrary))]
    private async Task AddLibraryAsync()
    {
        if (PickLibraryFolderAsync is null)
        {
            Status = "Folder picker is not available.";
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

        if (_pendingRemoveLibraryRoot is null ||
            !StringComparer.OrdinalIgnoreCase.Equals(_pendingRemoveLibraryRoot, SelectedLibraryRoot))
        {
            _pendingRemoveLibraryRoot = SelectedLibraryRoot;
            RemoveLibraryButtonText = "Confirm Remove";
            Status = $"Click Confirm Remove within 5 seconds to forget {SelectedLibraryRoot}. Files will not be deleted.";
            ExpireRemoveLibraryConfirmationAfterDelay(SelectedLibraryRoot, ++_removeLibraryConfirmationVersion);
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
        }
        catch (Exception ex)
        {
            Status = $"Failed to remove library path: {ex.Message}";
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
        }
        catch (Exception ex)
        {
            Status = $"Failed to set library location: {ex.Message}";
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
        _pendingDeleteTag = null;
        _deleteConfirmationVersion++;
        DeleteButtonText = "Delete";
    }

    private async void ExpireDeleteConfirmationAfterDelay(string tag, int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (_deleteConfirmationVersion != version || _pendingDeleteTag != tag)
            return;

        ClearPendingDelete();
        Status = $"Delete confirmation expired for {tag}.";
    }

    private void ClearPendingRemoveLibrary()
    {
        _pendingRemoveLibraryRoot = null;
        _removeLibraryConfirmationVersion++;
        RemoveLibraryButtonText = "Remove path";
    }

    private async void ExpireRemoveLibraryConfirmationAfterDelay(string libraryRoot, int version)
    {
        await Task.Delay(TimeSpan.FromSeconds(5));
        if (_removeLibraryConfirmationVersion != version ||
            !StringComparer.OrdinalIgnoreCase.Equals(_pendingRemoveLibraryRoot, libraryRoot))
            return;

        ClearPendingRemoveLibrary();
        Status = $"Remove path confirmation expired for {libraryRoot}.";
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

    private void NotifyCommandStatesChanged()
    {
        RefreshReleasesCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
        PlayCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
        AddLibraryCommand.NotifyCanExecuteChanged();
        RemoveLibraryCommand.NotifyCanExecuteChanged();
    }
}
