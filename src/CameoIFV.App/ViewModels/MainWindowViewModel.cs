using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CameoIFV.App.Services;
using CameoIFV.Core.Github;
using CameoIFV.Core.Install;
using CameoIFV.Core.Model;
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

    [ObservableProperty] private ModDefinition? _selectedMod;
    [ObservableProperty] private ChannelOption? _selectedChannel;
    [ObservableProperty] private ResolvedRelease? _selectedRelease;
    [ObservableProperty] private InstalledInstance? _selectedInstance;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isBusy;

    public MainWindowViewModel() : this(new LauncherServices()) { }

    public MainWindowViewModel(LauncherServices services)
    {
        _services = services;
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
    }

    partial void OnSelectedChannelChanged(ChannelOption? value)
    {
        _ = RefreshReleasesAsync();
    }

    [RelayCommand]
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

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (SelectedMod is null || SelectedRelease is null || IsBusy)
            return;

        var mod = SelectedMod;
        var release = SelectedRelease;

        IsBusy = true;
        Progress = 0;
        try
        {
            var progress = new Progress<InstallProgress>(p =>
            {
                Progress = p.Fraction * 100;
                Status = p.TotalBytes > 0
                    ? $"{p.Phase}: {release.TagName} ({Progress:F0}%)"
                    : $"{p.Phase}: {release.TagName}...";
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

    [RelayCommand]
    private void Play()
    {
        if (SelectedInstance is null)
            return;

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

    [RelayCommand]
    private void Delete()
    {
        if (SelectedInstance is null || IsBusy)
            return;

        try
        {
            _services.Instances.Delete(SelectedInstance);
            Status = $"Deleted {SelectedInstance.Tag}.";
            RefreshInstalled();
        }
        catch (Exception ex)
        {
            Status = $"Delete failed: {ex.Message}";
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
    }
}
