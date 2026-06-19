using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using System.Linq;
using Avalonia.Markup.Xaml;
using CameoIFV.App.ViewModels;
using CameoIFV.App.Views;

namespace CameoIFV.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = new MainWindowViewModel();
            desktop.MainWindow = new MainWindow
            {
                DataContext = viewModel,
            };

#if !DEBUG
            // Auto-check for a newer launcher on startup in shipped builds only; dev builds report an
            // un-injected version and would otherwise offer to "update" to the latest release.
            viewModel.BeginStartupUpdateCheck();
#endif
        }

        base.OnFrameworkInitializationCompleted();
    }
}