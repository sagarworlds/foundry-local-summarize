using Microsoft.Extensions.DependencyInjection;
using FoundrySummarizer.Core.Chat;
using FoundrySummarizer.Core.Ingestion;
using FoundrySummarizer.Core.Routing;
using FoundrySummarizer.Core.Summarization;
using FoundrySummarizer.Presentation.Services;
using FoundrySummarizer.Presentation.ViewModels;
using FoundrySummarizer.Wpf.Services;

namespace FoundrySummarizer.Wpf.Tests;

/// <summary>The startup wiring: settings are read, and every service the app asks for can be built.</summary>
public class AppServicesTests
{
    /// <summary>Options that never reach a real Foundry Local, even on a developer machine that has one.</summary>
    private static FoundryOptions Offline()
    {
        var options = new FoundryOptions();
        options.Local.AutoDiscover = false;
        options.Local.AutoStartService = false;
        options.Local.Endpoint = "http://127.0.0.1:1/v1";   // nothing listens on port 1
        return options;
    }

    [Fact]
    public void EveryRegisteredServiceCanBeBuilt()
    {
        // ValidateOnBuild checks every constructor's dependencies without creating the UI.
        using var provider = new ServiceCollection().AddSummarizerApp(Offline())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.IsType<MultiPartSummarizer>(provider.GetRequiredService<IDocumentSummarizer>());
        Assert.IsType<FollowUpQuestionGenerator>(provider.GetRequiredService<IFollowUpQuestionGenerator>());
        Assert.IsType<DocumentIngestionPipeline>(provider.GetRequiredService<IDocumentIngestionPipeline>());
        Assert.IsType<OpenFileDocumentPicker>(provider.GetRequiredService<IDocumentPicker>());
        Assert.IsType<WpfClipboardService>(provider.GetRequiredService<IClipboardService>());
        Assert.IsType<JsonUserSettingsStore>(provider.GetRequiredService<IUserSettingsStore>());
        Assert.NotNull(provider.GetRequiredService<DocumentChatAgent>());
    }

    [Fact]
    public void SummariesAndChat_ShareOneModelClient_AndOneReadinessState()
    {
        using var provider = new ServiceCollection().AddSummarizerApp(Offline()).BuildServiceProvider();

        // One client means Foundry Local is found and the model loaded once for both screens.
        Assert.Same(provider.GetRequiredService<FoundryLocalChatClient>(), provider.GetRequiredService<FoundryLocalChatClient>());

        // The screens are enabled by the same object that loads the model.
        var picker = provider.GetRequiredService<ModelPickerViewModel>();
        Assert.Same(picker, provider.GetRequiredService<IModelReadiness>());
        Assert.Same(picker, provider.GetRequiredService<MainViewModel>().ModelPicker);
    }

    [Fact]
    public void TheShippedSettings_AreValid()
    {
        // appsettings.json is copied next to the app (and these tests) at build time.
        var options = AppServices.ReadFoundryOptions(AppServices.BuildConfiguration(AppContext.BaseDirectory));

        Assert.True(options.Local.AutoDiscover);
        Assert.NotEmpty(options.Local.PreferredModels);
        Assert.Equal("phi-4-mini", options.Local.PreferredModels[0]);
        Assert.True(options.Local.TimeoutSeconds > 0);
        Assert.True(options.Summarization.MaxSinglePassTokens > options.Summarization.MapChunkTokens);
        Assert.True(options.Chat.MaxPassageTokens > 0);
    }

    [Fact]
    public void SettingsFile_OverridesDefaults_AndMissingValuesKeepThem()
    {
        var folder = Directory.CreateTempSubdirectory("summarizer-settings-");
        try
        {
            File.WriteAllText(Path.Combine(folder.FullName, "appsettings.json"),
                """{"Foundry":{"Local":{"ModelId":"llama3.2:3b","AutoDiscover":false},"Chat":{"MaxHistoryTurns":2}}}""");

            var options = AppServices.ReadFoundryOptions(AppServices.BuildConfiguration(folder.FullName));

            Assert.Equal("llama3.2:3b", options.Local.ModelId);
            Assert.False(options.Local.AutoDiscover);
            Assert.Equal(2, options.Chat.MaxHistoryTurns);
            Assert.Equal(new FoundryOptions().Chat.MaxAnswerTokens, options.Chat.MaxAnswerTokens);   // not in the file
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void NoSettingsFile_UsesDefaults()
    {
        var folder = Directory.CreateTempSubdirectory("summarizer-empty-");
        try
        {
            var options = AppServices.ReadFoundryOptions(AppServices.BuildConfiguration(folder.FullName));

            Assert.Equal(new FoundryOptions().Local.ModelId, options.Local.ModelId);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }
}
