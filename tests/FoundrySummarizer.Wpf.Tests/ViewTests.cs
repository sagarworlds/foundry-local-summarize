using System.Diagnostics;
using System.Windows;
using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Chat;
using FoundrySummarizer.Core.Ingestion;
using FoundrySummarizer.Core.Personas;
using FoundrySummarizer.Core.Routing;
using FoundrySummarizer.Core.Summarization;
using FoundrySummarizer.Presentation.Services;
using FoundrySummarizer.Presentation.ViewModels;
using FoundrySummarizer.Wpf.Views;

namespace FoundrySummarizer.Wpf.Tests;

/// <summary>
/// Builds the real window and views with the app's styles and checks that they load and that every binding finds
/// its view-model property. A renamed property or a missing style otherwise only shows up when someone runs the app.
/// </summary>
public class ViewTests
{
    /// <summary>Collects WPF's data-binding errors (they are only traced, never thrown).</summary>
    private sealed class BindingErrors : TraceListener
    {
        private readonly List<string> _errors = new();

        public IReadOnlyList<string> PathErrors
        {
            get { lock (_errors) return _errors.Where(e => e.Contains("BindingExpression path error")).ToList(); }
        }

        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (message is not null) lock (_errors) _errors.Add(message);
        }
    }

    private sealed class Answers : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "It is $150,000 [P1].")));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    private sealed class Questions : IFollowUpQuestionGenerator
    {
        public Task<IReadOnlyList<string>> SuggestAsync(string documentName, string summary, string documentText, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { "What is the budget?", "Who approves it?" });
    }

    private sealed class Summarizer : IDocumentSummarizer
    {
        public Task<SummarizationResult> SummarizeAsync(SummarizationRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SummarizationResult("- Budget: $150,000", false, 1));
    }

    private sealed class Settings : IUserSettingsStore
    {
        public UserSettings Load() => new();
        public string? Save(UserSettings settings) => null;
    }

    private sealed class NoPicker : IDocumentPicker
    {
        public string? PickDocument(IReadOnlyCollection<string> supportedExtensions) => null;
    }

    private sealed class NoClipboard : IClipboardService
    {
        public string? TrySetText(string text) => null;
    }

    /// <summary>The app's view models with test doubles; Foundry Local is unreachable, so the "no model" banner shows.</summary>
    private static MainViewModel CreateScreens()
    {
        var options = new FoundryOptions();
        options.Local.AutoDiscover = false;
        options.Local.AutoStartService = false;
        options.Local.Endpoint = "http://127.0.0.1:1/v1";
        var activity = new ActivityTracker();
        var picker = new ModelPickerViewModel(new FoundryLocalChatClient(options), new Settings(), activity, TimeSpan.FromHours(1));
        var summarizer = new SummarizerViewModel(new DocumentIngestionPipeline(), new PromptyEngine(), new Summarizer(),
            new NoPicker(), new NoClipboard(), new SummarizationConfig(), picker, activity);
        var chat = new ChatViewModel(new DocumentChatAgent(new Answers()), new Questions(), picker, activity);
        return new MainViewModel(picker, summarizer, chat);
    }

    [Fact]
    public async Task MainWindow_Loads_AndEveryBindingResolves()
    {
        var errors = new BindingErrors();
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
        PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        try
        {
            var (window, main) = UiThread.Invoke(() =>
            {
                var screens = CreateScreens();
                var w = new MainWindow { DataContext = screens, ShowActivated = false, ShowInTaskbar = false };
                w.Show();
                return (w, screens);
            });
            UiThread.Flush();

            // Fill both screens so the templates for messages, suggestions and the summary are built too.
            var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-minutes.txt");
            File.WriteAllText(path, "Budget is $150,000. Approval is due Friday.");
            try
            {
                await UiThread.Invoke(() => main.SummarizerVm.LoadDocumentAsync(path));
                UiThread.Invoke(() =>
                {
                    main.ChatVm.UpdateSummary("- Budget: $150,000");
                    main.SelectTabCommand.Execute("1");
                });
                UiThread.Flush();
                await Task.Delay(200);                                    // suggestions arrive asynchronously
                UiThread.Flush();

                UiThread.Invoke(() =>
                {
                    Assert.True(main.IsChatTabSelected);
                    Assert.NotEmpty(main.ChatVm.SuggestedQuestions);
                    Assert.True(window.IsLoaded);
                    window.Close();
                });
            }
            finally
            {
                File.Delete(path);
            }

            Assert.Empty(errors.PathErrors);
        }
        finally
        {
            PresentationTraceSources.DataBindingSource.Listeners.Remove(errors);
        }
    }

    [Fact]
    public void Views_LoadWithTheAppStyles()
    {
        // A missing StaticResource or a XAML typo throws while the view is built.
        UiThread.Invoke(() =>
        {
            Assert.NotNull(new SummarizerView());
            Assert.NotNull(new ChatView());
        });
    }
}
