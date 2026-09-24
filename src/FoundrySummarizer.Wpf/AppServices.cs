using System.IO;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using FoundrySummarizer.Core.Chat;
using FoundrySummarizer.Core.Ingestion;
using FoundrySummarizer.Core.Personas;
using FoundrySummarizer.Core.Routing;
using FoundrySummarizer.Core.Summarization;
using FoundrySummarizer.Presentation.Services;
using FoundrySummarizer.Presentation.ViewModels;
using FoundrySummarizer.Wpf.Services;

namespace FoundrySummarizer.Wpf;

/// <summary>
/// Reads the app's settings and registers its services. Kept apart from <see cref="App"/> so tests can check the
/// configuration and the container without starting the user interface.
/// </summary>
public static class AppServices
{
    /// <summary>
    /// appsettings(.{environment}).json in <paramref name="baseDirectory"/>, then the project folder copy (when running
    /// from source), then environment variables; later sources override earlier ones.
    /// </summary>
    /// <param name="baseDirectory">The folder the app runs from (normally <see cref="AppContext.BaseDirectory"/>).</param>
    public static IConfiguration BuildConfiguration(string baseDirectory)
    {
        var builder = new ConfigurationBuilder()
            .SetBasePath(baseDirectory)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile($"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Production"}.json", optional: true);

        var projectConfig = Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "appsettings.json"));
        if (File.Exists(projectConfig))
        {
            builder.AddJsonFile(projectConfig, optional: true, reloadOnChange: true);
        }

        return builder.AddEnvironmentVariables().Build();
    }

    /// <summary>Binds the <c>Foundry</c> section; missing values keep their defaults.</summary>
    /// <param name="configuration">Settings from <see cref="BuildConfiguration"/>.</param>
    public static FoundryOptions ReadFoundryOptions(IConfiguration configuration)
    {
        var options = new FoundryOptions();
        configuration.GetSection(FoundryOptions.SectionName).Bind(options);
        return options;
    }

    /// <summary>Registers every service, view model and the main window.</summary>
    /// <param name="services">The container to fill.</param>
    /// <param name="foundryOptions">Foundry Local, summarization and chat settings.</param>
    /// <returns><paramref name="services"/>, for chaining.</returns>
    public static IServiceCollection AddSummarizerApp(this IServiceCollection services, FoundryOptions foundryOptions)
    {
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

        services.AddSingleton<IFollowUpQuestionGenerator>(sp => new FollowUpQuestionGenerator(sp.GetRequiredService<FoundryLocalChatClient>()));

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
        return services;
    }
}
