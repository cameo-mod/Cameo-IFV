using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CameoIFV.App.ViewModels;

namespace CameoIFV.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.PickLibraryFolderAsync = PickLibraryFolderAsync;
            viewModel.OpenFolder = OpenFolder;
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

    private static void OpenFolder(string path)
    {
        if (!Directory.Exists(path))
            return;

        using var _ = Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true,
        });
    }
}
