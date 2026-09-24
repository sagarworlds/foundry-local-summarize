using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using FoundrySummarizer.Core.Evaluation;
using FoundrySummarizer.Core.Grounding;
using FoundrySummarizer.Core.Ingestion;
using FoundrySummarizer.Core.Personas;
using FoundrySummarizer.Core.Routing;
using FoundrySummarizer.Core.Summarization;
using FoundrySummarizer.Wpf.ViewModels;

namespace FoundrySummarizer.Wpf;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. Build Configuration from appsettings.json, environment variables, etc.
        var baseDir = AppContext.BaseDirectory;
        var configBuilder = new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true)
            .AddEnvironmentVariables();

        // Also check parent directories for appsettings.json during development if needed
        var rootConfig = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "appsettings.json"));
        if (File.Exists(rootConfig))
        {
            configBuilder.AddJsonFile(rootConfig, optional: true, reloadOnChange: true);
        }

        var configuration = configBuilder.Build();

        // 2. Bind Foundry Options
        var foundryOptions = new FoundryOptions();
        configuration.GetSection(FoundryOptions.SectionName).Bind(foundryOptions);

        // 3. Configure Dependency Injection
        var services = new ServiceCollection();

        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton(foundryOptions);

        services.AddSingleton<HybridChatClientRouter>();
        services.AddSingleton<IDocumentIngestionPipeline, DocumentIngestionPipeline>();
        services.AddSingleton<IPromptyEngine, PromptyEngine>();
        services.AddSingleton<IVectorGroundingService, VectorGroundingService>();
        services.AddSingleton<IEvaluationPipeline, EvaluationPipeline>();
        services.AddSingleton<IDocumentSummarizer>(sp => new MultiPartSummarizer(
            sp.GetRequiredService<HybridChatClientRouter>(),
            sp.GetRequiredService<IPromptyEngine>(),
            foundryOptions.Summarization));

        // Register ViewModels and MainWindow
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        _serviceProvider = services.BuildServiceProvider();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.DataContext = _serviceProvider.GetRequiredService<MainViewModel>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
