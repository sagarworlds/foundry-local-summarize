using System.IO;
using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using FoundrySummarizer.Core.Chat;
using FoundrySummarizer.Core.Ingestion;
using FoundrySummarizer.Core.Personas;
using FoundrySummarizer.Core.Routing;
using FoundrySummarizer.Core.Summarization;
using FoundrySummarizer.Wpf.Services;
using FoundrySummarizer.Wpf.ViewModels;

namespace FoundrySummarizer.Wpf;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var foundryOptions = new FoundryOptions();
        BuildConfiguration().GetSection(FoundryOptions.SectionName).Bind(foundryOptions);

        var services = new ServiceCollection();
        services.AddSingleton(foundryOptions);
        services.AddSingleton(foundryOptions.Summarization);
        services.AddSingleton(foundryOptions.Chat);

        // One client for summaries and chat, so the Foundry Local service is found and the model loaded once.
        services.AddSingleton<FoundryLocalChatClient>();
        services.AddSingleton<IDocumentIngestionPipeline, DocumentIngestionPipeline>();
        services.AddSingleton<IPromptyEngine, PromptyEngine>();
        services.AddSingleton<IDocumentSummarizer>(sp => new MultiPartSummarizer(
            sp.GetRequiredService<FoundryLocalChatClient>(),
            sp.GetRequiredService<IPromptyEngine>(),
            sp.GetRequiredService<SummarizationConfig>()));
        services.AddSingleton(sp => new DocumentChatAgent(
            sp.GetRequiredService<FoundryLocalChatClient>(),
            config: sp.GetRequiredService<ChatConfig>()));

        services.AddSingleton<IDocumentPicker, OpenFileDocumentPicker>();
        services.AddSingleton<IClipboardService, WpfClipboardService>();
        services.AddSingleton<IUserSettingsStore, JsonUserSettingsStore>();
        services.AddSingleton<IActivityTracker, ActivityTracker>();

        // The picker owns the model state; the screens only see whether a model is ready (IModelReadiness).
        services.AddSingleton<ModelPickerViewModel>();
        services.AddSingleton<IModelReadiness>(sp => sp.GetRequiredService<ModelPickerViewModel>());

        services.AddSingleton<SummarizerViewModel>();
        services.AddSingleton<ChatViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        _serviceProvider = services.BuildServiceProvider();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.DataContext = _serviceProvider.GetRequiredService<MainViewModel>();
        mainWindow.Show();
    }

    /// <summary>appsettings(.{environment}).json next to the executable, then the repository root copy (for development), then environment variables.</summary>
    private static IConfiguration BuildConfiguration()
    {
        var baseDir = AppContext.BaseDirectory;
        var builder = new ConfigurationBuilder()
            .SetBasePath(baseDir)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true);

        var repoRootConfig = Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "appsettings.json"));
        if (File.Exists(repoRootConfig))
        {
            builder.AddJsonFile(repoRootConfig, optional: true, reloadOnChange: true);
        }

        return builder.AddEnvironmentVariables().Build();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }
}
