using System.Windows;
using Microsoft.Extensions.DependencyInjection;

namespace FoundrySummarizer.Wpf;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var foundryOptions = AppServices.ReadFoundryOptions(AppServices.BuildConfiguration(AppContext.BaseDirectory));
        _serviceProvider = new ServiceCollection().AddSummarizerApp(foundryOptions).BuildServiceProvider();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.DataContext = _serviceProvider.GetRequiredService<Presentation.ViewModels.MainViewModel>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
