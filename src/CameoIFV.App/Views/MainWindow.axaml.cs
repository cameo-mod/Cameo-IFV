using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CameoIFV.App.ViewModels;

namespace CameoIFV.App.Views;

public partial class MainWindow : Window
{
    private bool _sessionLogAutoScrollEnabled = true;
    private ScrollViewer? _sessionLogScrollViewer;
    private bool _sessionLogScrollViewerHooked;

    public MainWindow()
    {
        InitializeComponent();
        SessionLogTextBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty)
                AutoScrollSessionLog();
        };
        SessionLogTextBox.PointerWheelChanged += OnSessionLogPointerWheelChanged;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        HookSessionLogScrollViewer();
        AutoScrollSessionLog();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.PickLibraryFolderAsync = PickLibraryFolderAsync;
            viewModel.RequestRelaunch = RelaunchTo;
        }
    }

    // Launch the freshly-installed launcher executable and shut this instance down so its (now
    // renamed-aside) old exe is released and swept on the next start.
    private void RelaunchTo(string executablePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath),
                UseShellExecute = false,
            });
        }
        catch
        {
            // If we couldn't even start the new exe, leave this window open for a manual restart.
            return;
        }

        // The new instance is starting; shut this one down. Guard the teardown: a Shutdown()/Close()
        // hiccup must not bubble up (it previously surfaced as a misleading "update failed"), and we
        // must not linger beside the new instance — so force-exit if the graceful path throws.
        try
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
            else
                Close();
        }
        catch
        {
            Environment.Exit(0);
        }
    }

    private async Task<string?> PickLibraryFolderAsync(string? currentPath)
    {
        var startLocation = !string.IsNullOrWhiteSpace(currentPath) && Directory.Exists(currentPath)
            ? await StorageProvider.TryGetFolderFromPathAsync(currentPath)
            : null;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose Cameo-IFV Library Folder",
            AllowMultiple = false,
            SuggestedStartLocation = startLocation,
        });

        return folders.Count > 0 ? folders[0].Path.LocalPath : null;
    }

    private void HookSessionLogScrollViewer()
    {
        _sessionLogScrollViewer ??= SessionLogTextBox
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault();

        if (_sessionLogScrollViewer is null || _sessionLogScrollViewerHooked)
            return;

        _sessionLogScrollViewerHooked = true;
    }

    private void OnSessionLogPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y > 0)
        {
            _sessionLogAutoScrollEnabled = false;
            return;
        }

        if (e.Delta.Y < 0)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (IsSessionLogAtBottom())
                    _sessionLogAutoScrollEnabled = true;
            });
        }
    }

    private void AutoScrollSessionLog()
    {
        if (!_sessionLogAutoScrollEnabled)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (!_sessionLogAutoScrollEnabled)
                return;

            HookSessionLogScrollViewer();
            SessionLogTextBox.CaretIndex = SessionLogTextBox.Text?.Length ?? 0;
            _sessionLogScrollViewer?.ScrollToEnd();
        });
    }

    private bool IsSessionLogAtBottom()
    {
        HookSessionLogScrollViewer();
        if (_sessionLogScrollViewer is null)
            return true;

        var scrollable = _sessionLogScrollViewer.Extent.Height - _sessionLogScrollViewer.Viewport.Height;
        return _sessionLogScrollViewer.Offset.Y >= scrollable - 1;
    }
}
