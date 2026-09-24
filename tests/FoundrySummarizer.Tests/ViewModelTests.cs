using System.Net;
using Microsoft.Extensions.AI;
using FoundrySummarizer.Core.Chat;
using FoundrySummarizer.Core.Ingestion;
using FoundrySummarizer.Core.Personas;
using FoundrySummarizer.Core.Routing;
using FoundrySummarizer.Core.Summarization;
using FoundrySummarizer.Presentation.Services;
using FoundrySummarizer.Presentation.ViewModels;

namespace FoundrySummarizer.Tests;

/// <summary>Test doubles shared by the screen (view model) tests.</summary>
internal static class Screen
{
    /// <summary>Waits for background work started by a view model (e.g. model loading) to reach a state.</summary>
    public static async Task Until(Func<bool> condition, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The expected state was not reached.");
            await Task.Delay(20);
        }
    }

    public sealed class Readiness : IModelReadiness
    {
        private bool _ready = true;
        public bool IsModelReady { get => _ready; set { _ready = value; ReadinessChanged?.Invoke(this, EventArgs.Empty); } }
        public bool IsModelLoading { get; set; }
        public event EventHandler? ReadinessChanged;
    }

    public sealed class Settings : IUserSettingsStore
    {
        public UserSettings Saved { get; set; } = new();
        /// <summary>When set, saving "fails" with this reason (the choice is still kept in memory).</summary>
        public string? Problem { get; set; }
        public UserSettings Load() => Saved;
        public string? Save(UserSettings settings) { Saved = settings; return Problem; }
    }

    public sealed class Picker(string? path) : IDocumentPicker
    {
        public string? PickDocument(IReadOnlyCollection<string> supportedExtensions) => path;
    }

    public sealed class Clipboard(string? problem = null) : IClipboardService
    {
        public string? Text { get; private set; }
        public string? TrySetText(string text) { Text = text; return problem; }
    }

    public sealed class Summarizer(Func<SummarizationRequest, CancellationToken, Task<SummarizationResult>> summarize) : IDocumentSummarizer
    {
        public Task<SummarizationResult> SummarizeAsync(SummarizationRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            progress?.Report("Reading part 1 of 2...");
            return summarize(request, cancellationToken);
        }
    }

    public sealed class Answers(Func<IList<ChatMessage>, string> answer) : IChatClient
    {
        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer(messages.ToList()))));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    /// <summary>A model that never answers on its own; only cancellation ends the request.</summary>
    public sealed class SilentModel : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }

    public sealed class Questions : IFollowUpQuestionGenerator
    {
        public Func<string, CancellationToken, Task<IReadOnlyList<string>>> Suggest { get; set; } =
            (summary, _) => Task.FromResult<IReadOnlyList<string>>(new[] { $"About {summary}?" });

        public Task<IReadOnlyList<string>> SuggestAsync(string documentName, string summary, string documentText, CancellationToken cancellationToken = default) =>
            Suggest(summary, cancellationToken);
    }

    public static string TempFile(string name, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-{name}");
        File.WriteAllText(path, content);
        return path;
    }
}

public class SummarizerScreenTests
{
    private static SummarizerViewModel Create(
        Screen.Summarizer? summarizer = null,
        Screen.Readiness? readiness = null,
        string? pickedFile = null,
        Screen.Clipboard? clipboard = null,
        IActivityTracker? activity = null) =>
        new(new DocumentIngestionPipeline(), new PromptyEngine(),
            summarizer ?? new Screen.Summarizer((r, _) => Task.FromResult(new SummarizationResult($"SUMMARY of {r.DocumentText.Length} chars", false, 1))),
            new Screen.Picker(pickedFile), clipboard ?? new Screen.Clipboard(), new SummarizationConfig { MaxSinglePassTokens = 50 },
            readiness ?? new Screen.Readiness(), activity ?? new ActivityTracker());

    [Fact]
    public async Task OpeningADocument_ShowsItsText_AndStartsANewChat()
    {
        var path = Screen.TempFile("minutes.txt", "Budget is $150,000.");
        var vm = Create(pickedFile: path);
        (string Name, string Text)? loaded = null;
        vm.DocumentLoaded += (name, text) => loaded = (name, text);

        await vm.OpenDocumentCommand.ExecuteAsync(null);

        Assert.True(vm.HasDocument);
        Assert.Equal("Budget is $150,000.", vm.DocumentText);
        Assert.Equal(Path.GetFileName(path), loaded?.Name);
        Assert.StartsWith("Loaded", vm.Status);
        Assert.False(vm.IsError);
        File.Delete(path);
    }

    [Fact]
    public async Task ALongDocument_SaysItWillBeReadInParts()
    {
        var path = Screen.TempFile("long.txt", string.Join(" ", Enumerable.Repeat("word", 400)));
        var vm = Create();

        await vm.LoadDocumentAsync(path);

        Assert.Contains("will be read in parts", vm.Status);
        File.Delete(path);
    }

    [Fact]
    public async Task ADocumentWithoutText_IsReported_AndNothingCanBeSummarized()
    {
        var path = Screen.TempFile("empty.txt", "   ");
        var vm = Create();
        var loadedEvents = 0;
        vm.DocumentLoaded += (_, _) => loadedEvents++;

        await vm.LoadDocumentAsync(path);

        Assert.True(vm.IsError);
        Assert.Contains("No text could be extracted", vm.Status);
        Assert.Equal(0, loadedEvents);
        Assert.False(vm.GenerateSummaryCommand.CanExecute(null));
        File.Delete(path);
    }

    [Fact]
    public async Task AnUnreadableOrUnsupportedFile_IsReported()
    {
        var corrupt = Screen.TempFile("broken.docx", "not a zip package");
        var unsupported = Screen.TempFile("song.mp3", "ID3");
        var vm = Create();

        await vm.LoadDocumentAsync(corrupt);
        Assert.True(vm.IsError);
        Assert.Contains("Could not read broken.docx", vm.Status[..(vm.Status.IndexOf(':') + 1)].Replace(Path.GetFileName(corrupt), "broken.docx") + vm.Status);

        await vm.LoadDocumentAsync(unsupported);
        Assert.True(vm.IsError);
        Assert.Contains("is not supported", vm.Status);

        File.Delete(corrupt);
        File.Delete(unsupported);
    }

    [Fact]
    public async Task Summarizing_ShowsTheSummary_AndHandsItToTheChat()
    {
        var path = Screen.TempFile("minutes.txt", "Budget is $150,000.");
        var vm = Create();
        await vm.LoadDocumentAsync(path);
        string? handedOver = null;
        vm.SummaryGenerated += s => handedOver = s;

        await vm.GenerateSummaryCommand.ExecuteAsync(null);

        Assert.Equal("SUMMARY of 19 chars", vm.Summary);
        Assert.Equal(vm.Summary, handedOver);
        Assert.StartsWith("Summary ready", vm.Status);
        File.Delete(path);
    }

    [Fact]
    public async Task ModelFailure_IsShown_AndNoSummaryAppears()
    {
        var path = Screen.TempFile("minutes.txt", "Budget is $150,000.");
        var vm = Create(summarizer: new Screen.Summarizer((_, _) => throw new LocalModelUnavailableException("Foundry Local is not running.")));
        await vm.LoadDocumentAsync(path);

        await vm.GenerateSummaryCommand.ExecuteAsync(null);

        Assert.True(vm.IsError);
        Assert.Equal("⚠️ No summary: Foundry Local is not running.", vm.Status);
        Assert.False(vm.HasSummary);
        File.Delete(path);
    }

    [Fact]
    public async Task Cancelling_StopsTheSummary()
    {
        var path = Screen.TempFile("minutes.txt", "Budget is $150,000.");
        var vm = Create(summarizer: new Screen.Summarizer(async (_, token) =>
        {
            await Task.Delay(Timeout.Infinite, token);
            return new SummarizationResult("never", false, 1);
        }));
        await vm.LoadDocumentAsync(path);

        var running = vm.GenerateSummaryCommand.ExecuteAsync(null);
        await Screen.Until(() => vm.GenerateSummaryCommand.IsRunning);
        vm.GenerateSummaryCancelCommand.Execute(null);
        await running;

        Assert.Equal("Summary cancelled.", vm.Status);
        Assert.False(vm.HasSummary);
        File.Delete(path);
    }

    [Fact]
    public async Task WhileSummarizing_TheModelIsMarkedInUse()
    {
        var path = Screen.TempFile("minutes.txt", "Budget is $150,000.");
        var release = new TaskCompletionSource();
        var activity = new ActivityTracker();
        var vm = Create(activity: activity, summarizer: new Screen.Summarizer(async (_, _) =>
        {
            await release.Task;
            return new SummarizationResult("done", false, 1);
        }));
        await vm.LoadDocumentAsync(path);

        var running = vm.GenerateSummaryCommand.ExecuteAsync(null);
        await Screen.Until(() => activity.IsBusy);
        Assert.False(vm.OpenDocumentCommand.CanExecute(null));   // no new document mid-summary
        release.SetResult();
        await running;

        Assert.False(activity.IsBusy);
        File.Delete(path);
    }

    [Fact]
    public async Task NothingCanBeDone_UntilAModelIsLoaded()
    {
        var path = Screen.TempFile("minutes.txt", "Budget is $150,000.");
        var readiness = new Screen.Readiness { IsModelReady = false };
        var vm = Create(readiness: readiness);
        await vm.LoadDocumentAsync(path);

        Assert.False(vm.OpenDocumentCommand.CanExecute(null));
        Assert.False(vm.GenerateSummaryCommand.CanExecute(null));

        readiness.IsModelReady = true;

        Assert.True(vm.OpenDocumentCommand.CanExecute(null));
        Assert.True(vm.GenerateSummaryCommand.CanExecute(null));
        File.Delete(path);
    }

    [Fact]
    public async Task Copying_PutsTheSummaryOnTheClipboard_OrSaysWhyNot()
    {
        var path = Screen.TempFile("minutes.txt", "Budget is $150,000.");
        var clipboard = new Screen.Clipboard();
        var vm = Create(clipboard: clipboard);
        await vm.LoadDocumentAsync(path);
        Assert.False(vm.CopySummaryCommand.CanExecute(null));   // nothing to copy yet

        await vm.GenerateSummaryCommand.ExecuteAsync(null);
        vm.CopySummaryCommand.Execute(null);
        Assert.Equal(vm.Summary, clipboard.Text);
        Assert.Equal("Summary copied to the clipboard.", vm.Status);

        var busy = Create(clipboard: new Screen.Clipboard("The clipboard is in use by another application."));
        await busy.LoadDocumentAsync(path);
        await busy.GenerateSummaryCommand.ExecuteAsync(null);
        busy.CopySummaryCommand.Execute(null);
        Assert.True(busy.IsError);
        Assert.Contains("clipboard is in use", busy.Status);
        File.Delete(path);
    }

    [Fact]
    public async Task SummarizingWithoutAStyle_AsksForOne()
    {
        var path = Screen.TempFile("minutes.txt", "Budget is $150,000.");
        var vm = Create();
        await vm.LoadDocumentAsync(path);
        vm.SelectedPersona = null;

        await vm.GenerateSummaryCommand.ExecuteAsync(null);

        Assert.Equal("Choose a summary style first.", vm.Status);
        Assert.True(vm.IsError);
        Assert.False(vm.HasSummary);
        File.Delete(path);
    }

    [Fact]
    public async Task LoadingANewDocument_ClearsTheOldSummary()
    {
        var first = Screen.TempFile("a.txt", "First document.");
        var second = Screen.TempFile("b.txt", "Second document.");
        var vm = Create();
        await vm.LoadDocumentAsync(first);
        await vm.GenerateSummaryCommand.ExecuteAsync(null);

        await vm.LoadDocumentAsync(second);

        Assert.False(vm.HasSummary);
        File.Delete(first);
        File.Delete(second);
    }
}

public class ChatScreenTests
{
    private static (ChatViewModel Vm, Screen.Questions Questions, Screen.Readiness Readiness, Screen.Answers Answers) Create(Func<IList<ChatMessage>, string>? answer = null)
    {
        var answers = new Screen.Answers(answer ?? (_ => "It is $150,000 [P1]."));
        var questions = new Screen.Questions();
        var readiness = new Screen.Readiness();
        var vm = new ChatViewModel(new DocumentChatAgent(answers), questions, readiness, new ActivityTracker());
        return (vm, questions, readiness, answers);
    }

    [Fact]
    public async Task AskingAQuestion_ShowsTheQuestionAndTheAnswer()
    {
        var (vm, _, _, _) = Create();
        vm.StartSession("minutes.txt", "Budget is $150,000.");
        vm.InputQuestion = "What is the budget?";

        await vm.SendMessageCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.InputQuestion);
        Assert.Equal(ChatSender.User, vm.Messages[^2].Sender);
        Assert.Equal("What is the budget?", vm.Messages[^2].Text);
        Assert.Equal(ChatSender.Assistant, vm.Messages[^1].Sender);
        Assert.Equal("It is $150,000 [P1].", vm.Messages[^1].Text);
        Assert.False(vm.IsThinking);
    }

    [Fact]
    public async Task ModelFailure_IsShownInTheConversation()
    {
        var (vm, _, _, _) = Create(_ => throw new LocalModelUnavailableException("Model 'phi-4-mini' did not answer within 300s."));
        vm.StartSession("minutes.txt", "Budget is $150,000.");
        vm.InputQuestion = "What is the budget?";

        await vm.SendMessageCommand.ExecuteAsync(null);

        Assert.Equal(ChatSender.Error, vm.Messages[^1].Sender);
        Assert.Contains("did not answer within 300s", vm.Messages[^1].Text);
    }

    [Fact]
    public void Questions_NeedADocument_AModel_AndText()
    {
        var (vm, _, readiness, _) = Create();
        vm.InputQuestion = "What is the budget?";
        Assert.False(vm.SendMessageCommand.CanExecute(null));          // no document

        vm.StartSession("minutes.txt", "Budget is $150,000.");
        Assert.True(vm.SendMessageCommand.CanExecute(null));

        readiness.IsModelReady = false;
        Assert.False(vm.SendMessageCommand.CanExecute(null));          // no model

        readiness.IsModelReady = true;
        vm.InputQuestion = "   ";
        Assert.False(vm.SendMessageCommand.CanExecute(null));          // nothing typed
    }

    [Fact]
    public async Task SuggestedQuestions_AreWrittenForEachSummary_AndCanBeAsked()
    {
        var (vm, _, _, answers) = Create();
        vm.StartSession("minutes.txt", "Budget is $150,000.");
        Assert.Empty(vm.SuggestedQuestions);
        Assert.Contains("Summarize the document", vm.SuggestionsStatus);

        vm.UpdateSummary("the budget");
        await Screen.Until(() => vm.SuggestedQuestions.Count == 1);

        Assert.Equal("About the budget?", vm.SuggestedQuestions[0]);
        Assert.Equal(string.Empty, vm.SuggestionsStatus);

        await vm.AskSuggestedQuestionCommand.ExecuteAsync("About the budget?");
        Assert.Equal("About the budget?", vm.Messages[^2].Text);
        Assert.Equal(1, answers.Calls);
    }

    [Fact]
    public async Task SuggestionFailure_IsShown()
    {
        var (vm, questions, _, _) = Create();
        questions.Suggest = (_, _) => throw new LocalModelUnavailableException("Foundry Local is not running.");
        vm.StartSession("minutes.txt", "Budget is $150,000.");

        vm.UpdateSummary("summary");
        await Screen.Until(() => !vm.IsSuggesting);

        Assert.Equal("Could not suggest questions: Foundry Local is not running.", vm.SuggestionsStatus);
        Assert.Empty(vm.SuggestedQuestions);
    }

    [Fact]
    public async Task SuggestionsForAnOldDocument_NeverAppear()
    {
        var (vm, questions, _, _) = Create();
        var slow = new TaskCompletionSource<IReadOnlyList<string>>();
        questions.Suggest = (_, token) => slow.Task.WaitAsync(token);
        vm.StartSession("old.txt", "Old document.");
        vm.UpdateSummary("old summary");
        await Screen.Until(() => vm.IsSuggesting);

        vm.StartSession("new.txt", "New document.");                   // replaces the document mid-generation
        slow.TrySetResult(new[] { "About the old document?" });
        await Task.Delay(100);

        Assert.Empty(vm.SuggestedQuestions);
        Assert.False(vm.IsSuggesting);
        Assert.Contains("Summarize the document", vm.SuggestionsStatus);
    }

    [Theory]
    [InlineData(ChatSender.User, "You", true)]
    [InlineData(ChatSender.Assistant, "Foundry Local", false)]
    [InlineData(ChatSender.Error, "⚠️ Could not answer", false)]
    [InlineData(ChatSender.Notice, "ℹ️", false)]
    public void Messages_AreLabelledBySender(ChatSender sender, string header, bool isUser)
    {
        var message = new ChatMessageItem(sender, "text", DateTime.Now);

        Assert.Equal(header, message.Header);
        Assert.Equal(isUser, message.IsUser);
    }

    [Fact]
    public async Task CancellingAQuestion_SaysSo_AndTheChatCanBeUsedAgain()
    {
        var readiness = new Screen.Readiness();
        var vm = new ChatViewModel(new DocumentChatAgent(new Screen.SilentModel()), new Screen.Questions(), readiness, new ActivityTracker());
        vm.StartSession("minutes.txt", "Budget is $150,000.");
        vm.InputQuestion = "What is the budget?";

        var asking = vm.SendMessageCommand.ExecuteAsync(null);
        await Screen.Until(() => vm.IsThinking);
        vm.SendMessageCancelCommand.Execute(null);
        await asking;

        Assert.Equal(ChatSender.Notice, vm.Messages[^1].Sender);
        Assert.Equal("Question cancelled.", vm.Messages[^1].Text);
        Assert.False(vm.IsThinking);
        vm.InputQuestion = "And the deadline?";
        Assert.True(vm.SendMessageCommand.CanExecute(null));
    }

    [Fact]
    public async Task ClearingTheConversation_KeepsTheDocument()
    {
        var (vm, _, _, _) = Create();
        vm.StartSession("minutes.txt", "Budget is $150,000.");
        vm.InputQuestion = "What is the budget?";
        await vm.SendMessageCommand.ExecuteAsync(null);

        vm.ClearChatCommand.Execute(null);

        Assert.Single(vm.Messages);
        Assert.Equal(ChatSender.Notice, vm.Messages[0].Sender);
        vm.InputQuestion = "And the deadline?";
        Assert.True(vm.SendMessageCommand.CanExecute(null));
    }
}

public class ModelPickerScreenTests
{
    private const string ServerStatus = "State    Ready\nWeb URLs http://127.0.0.1:56294";
    private const string ModelList = """
        │ Model Name   │ Type │ Cached │
        │ phi-4-mini   │ Chat │ ●      │
        │ qwen3-0.6b   │ Chat │ ●      │
        """;

    private static readonly Dictionary<string, string> Variants = new()
    {
        ["phi-4-mini"] = "Phi-4-mini-instruct-generic-gpu:5",
        ["qwen3-0.6b"] = "qwen3-0.6b-generic-gpu:1",
    };

    /// <summary>A Foundry Local 1.x+ fake whose loaded models and availability the test controls.</summary>
    private sealed class Foundry : HttpMessageHandler
    {
        public List<string> Loaded { get; } = new();
        public bool Up { get; set; } = true;
        public bool LoadFails { get; set; }
        public bool UnloadFails { get; set; }

        /// <summary>When set, reading the loaded models fails in a way the app does not expect (not a network error).</summary>
        public bool LoadedListThrows { get; set; }

        /// <summary>When set, model loads wait for it, so a test can observe the app while a model is loading.</summary>
        public TaskCompletionSource? LoadGate { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!Up) throw new HttpRequestException("Connection refused");
            if (LoadedListThrows && request.RequestUri!.AbsolutePath == "/models/loaded") throw new InvalidOperationException("Unexpected answer");
            var path = request.RequestUri!.AbsolutePath;
            var name = Uri.UnescapeDataString(path.Split('/').Last());
            if (path.StartsWith("/models/load/") && LoadGate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            lock (Loaded)
            {
                return (path switch
                {
                    "/status" => Json("""{"modelCachePath":"C:\\cache"}"""),
                    "/models/loaded" => Json(System.Text.Json.JsonSerializer.Serialize(Loaded)),
                    _ when path.StartsWith("/models/load/") && LoadFails => Json("""{"error":"Load failed"}""", HttpStatusCode.InternalServerError),
                    _ when !Variants.ContainsKey(name) => Json("""{"error":"Model not found"}""", HttpStatusCode.NotFound),
                    _ when path.StartsWith("/models/load/") => Add(Variants[name]),
                    _ when path.StartsWith("/models/unload/") && UnloadFails => Json("""{"error":"busy"}""", HttpStatusCode.InternalServerError),
                    _ when path.StartsWith("/models/unload/") => Remove(Variants[name]),
                    _ => Json("{}", HttpStatusCode.NotFound)
                });
            }
        }

        private HttpResponseMessage Add(string id) { if (!Loaded.Contains(id)) Loaded.Add(id); return Json("""{"status":"loaded"}"""); }
        private HttpResponseMessage Remove(string id) { Loaded.Remove(id); return Json("""{"status":"unloaded"}"""); }
        private static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json) };
    }

    private sealed class Cli : IFoundryCli
    {
        public Task<FoundryCliResult> RunAsync(string arguments, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FoundryCliResult(true, arguments == "model list" ? ModelList : ServerStatus, null));
    }

    private static (ModelPickerViewModel Vm, Foundry Foundry, Screen.Settings Settings, ActivityTracker Activity) Create(
        Action<Foundry>? setup = null, string? saved = null, TimeSpan? checkEvery = null, Action<FoundryOptions>? configure = null,
        string? saveProblem = null)
    {
        var foundry = new Foundry();
        setup?.Invoke(foundry);
        var options = new FoundryOptions();
        configure?.Invoke(options);
        var client = new FoundryLocalChatClient(options, new FoundryLocalService(options, new Cli(), foundry));
        var settings = new Screen.Settings { Saved = new UserSettings { SelectedModelId = saved }, Problem = saveProblem };
        var activity = new ActivityTracker();
        var vm = new ModelPickerViewModel(client, settings, activity, checkEvery ?? TimeSpan.FromHours(1));
        return (vm, foundry, settings, activity);
    }

    [Fact]
    public async Task AtStartup_ListsModels_AndLoadsTheBestOne()
    {
        var gate = new TaskCompletionSource();
        var (vm, foundry, _, _) = Create(f => f.LoadGate = gate);
        var readinessChanges = 0;
        vm.ReadinessChanged += (_, _) => readinessChanges++;

        // While the model loads, the app shows the loading screen, not the "no model" banner, and is not ready.
        await Screen.Until(() => vm.AvailableModels.Count > 0);
        Assert.True(vm.IsModelLoading);
        Assert.False(vm.IsModelReady);
        Assert.False(vm.ShowNotReadyBanner);
        Assert.False(vm.CanChangeModel);
        Assert.Contains("Loading phi-4-mini", vm.LoadingText);

        gate.SetResult();
        await Screen.Until(() => vm.IsModelReady);
        Assert.True(readinessChanges > 0);

        Assert.False(vm.IsModelLoading);
        Assert.Equal(new[] { "phi-4-mini", "qwen3-0.6b" }, vm.AvailableModels.Select(m => m.Id).Order());
        Assert.Equal("phi-4-mini", vm.SelectedModel?.Id);
        Assert.True(vm.SelectedModel?.IsLoaded);
        Assert.Equal(new[] { "Phi-4-mini-instruct-generic-gpu:5" }, foundry.Loaded);
        Assert.Contains("Ready", vm.StatusText);
    }

    [Fact]
    public async Task PickingAModel_UnloadsTheOldOne_LoadsTheNewOne_AndRemembersIt()
    {
        var (vm, foundry, settings, _) = Create();
        await Screen.Until(() => vm.IsModelReady);

        vm.SelectedModel = vm.AvailableModels.Single(m => m.Id == "qwen3-0.6b");
        await Screen.Until(() => vm.IsModelReady && !vm.IsModelLoading && foundry.Loaded.SequenceEqual(new[] { "qwen3-0.6b-generic-gpu:1" }));

        Assert.Equal("qwen3-0.6b", settings.Saved.SelectedModelId);
        Assert.Equal("Ready", vm.StatusText);
    }

    [Fact]
    public async Task PickingAModel_StillWorks_WhenTheOldOneCannotBeUnloaded_OrTheChoiceCannotBeSaved()
    {
        var (vm, foundry, _, _) = Create(f => f.UnloadFails = true, saveProblem: "Could not remember the model choice (disk full).");
        await Screen.Until(() => vm.IsModelReady);

        vm.SelectedModel = vm.AvailableModels.Single(m => m.Id == "qwen3-0.6b");
        await Screen.Until(() => vm.IsModelReady && !vm.IsModelLoading && foundry.Loaded.Contains("qwen3-0.6b-generic-gpu:1"));

        // Both problems are reported, but neither blocks the app: the new model is loaded and ready.
        Assert.StartsWith("Ready. Note: ", vm.StatusText);
        Assert.Contains("could not unload 'phi-4-mini'", vm.StatusText);
        Assert.Contains("Could not remember the model choice (disk full).", vm.StatusText);
        Assert.True(vm.AvailableModels.Single(m => m.Id == "phi-4-mini").IsLoaded);   // still in memory
    }

    [Fact]
    public async Task AConfiguredModelThatIsNotDownloaded_IsStillShownInTheList()
    {
        var (vm, _, _, _) = Create(configure: o => { o.Local.AutoSelectModel = false; o.Local.ModelId = "mistral-7b"; });

        await Screen.Until(() => vm.AvailableModels.Count > 0 && !vm.IsModelLoading);

        Assert.Equal("mistral-7b", vm.SelectedModel?.Id);
        Assert.Contains(vm.AvailableModels, m => m.Id == "mistral-7b" && !m.IsLoaded);
        Assert.False(vm.IsModelReady);                                    // Foundry Local has no such model
        Assert.True(vm.ShowNotReadyBanner);
    }

    [Fact]
    public async Task ARememberedModel_IsLoadedAtStartup()
    {
        var (vm, foundry, _, _) = Create(saved: "qwen3-0.6b");

        await Screen.Until(() => vm.IsModelReady);

        Assert.Equal("qwen3-0.6b", vm.SelectedModel?.Id);
        Assert.Equal(new[] { "qwen3-0.6b-generic-gpu:1" }, foundry.Loaded);
    }

    [Fact]
    public async Task ARememberedModelThatWasDeleted_FallsBackToAutomatic()
    {
        var (vm, _, settings, _) = Create(saved: "deleted-model");

        await Screen.Until(() => vm.IsModelReady);

        Assert.Equal("phi-4-mini", vm.SelectedModel?.Id);
        Assert.Null(settings.Saved.SelectedModelId);
    }

    [Fact]
    public async Task ALoadFailure_ShowsTheBanner_AndBlocksTheApp()
    {
        var (vm, _, _, _) = Create(f => f.LoadFails = true);

        await Screen.Until(() => !vm.IsModelLoading);

        Assert.False(vm.IsModelReady);
        Assert.True(vm.ShowNotReadyBanner);
        Assert.Contains("could not load 'phi-4-mini'", vm.StatusText);
    }

    [Fact]
    public async Task NoFoundryLocal_ShowsTheBanner()
    {
        var (vm, _, _, _) = Create(f => f.Up = false);

        await Screen.Until(() => !vm.IsModelLoading);

        Assert.False(vm.IsModelReady);
        Assert.True(vm.ShowNotReadyBanner);
        Assert.Contains("No local model service is reachable", vm.StatusText);
    }

    [Fact]
    public async Task TheModelCannotBeChanged_WhileASummaryOrAnswerIsRunning()
    {
        var (vm, _, _, activity) = Create();
        await Screen.Until(() => vm.IsModelReady);

        using (activity.Begin())
        {
            Assert.False(vm.CanChangeModel);
            Assert.False(vm.RefreshModelsCommand.CanExecute(null));
        }

        Assert.True(vm.CanChangeModel);
    }

    [Fact]
    public async Task AModelUnloadedByFoundryLocal_IsLoadedAgain()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ModelPickerViewModel.DefaultLoadedCheckInterval);   // the app's rate; tests check faster
        var (vm, foundry, _, _) = Create(checkEvery: TimeSpan.FromMilliseconds(100));
        await Screen.Until(() => vm.IsModelReady);

        lock (foundry.Loaded) foundry.Loaded.Clear();                   // idle time-to-live expired

        await Screen.Until(() => foundry.Loaded.Count == 1 && vm.IsModelReady && !vm.IsModelLoading);
        Assert.Equal(new[] { "Phi-4-mini-instruct-generic-gpu:5" }, foundry.Loaded);
    }

    [Fact]
    public async Task AnOutage_TurnsTheAppNotReady_AndItRecoversWhenFoundryLocalReturns()
    {
        var (vm, foundry, _, _) = Create(checkEvery: TimeSpan.FromMilliseconds(100));
        await Screen.Until(() => vm.IsModelReady);

        foundry.Up = false;
        await Screen.Until(() => !vm.IsModelReady);
        Assert.Contains("Click ↻ to reconnect", vm.StatusText);

        foundry.Up = true;                                               // back, model still loaded
        await Screen.Until(() => vm.IsModelReady);
        Assert.Contains("Ready", vm.StatusText);
    }

    [Fact]
    public async Task AModelThatFailedToLoad_StaysNotReady_AndTheCheckSaysWhatIsLoaded()
    {
        // The user picked phi-4-mini, it failed to load, and another model is loaded (e.g. from a terminal).
        var (vm, foundry, _, _) = Create(f => { f.LoadFails = true; f.Loaded.Add("qwen3-0.6b-generic-gpu:1"); },
            saved: "phi-4-mini", checkEvery: TimeSpan.FromMilliseconds(100));
        await Screen.Until(() => !vm.IsModelLoading);

        await Screen.Until(() => vm.StatusText.Contains("Pick the loaded model"));

        Assert.False(vm.IsModelReady);                                    // not loaded again automatically
        Assert.Contains("qwen3-0.6b-generic-gpu:1", vm.StatusText);
        Assert.Equal(new[] { "qwen3-0.6b-generic-gpu:1" }, foundry.Loaded);
    }

    [Fact]
    public async Task AnUnexpectedErrorWhileChecking_IsShown_AndCheckingContinues()
    {
        var (vm, foundry, _, _) = Create(checkEvery: TimeSpan.FromMilliseconds(100));
        await Screen.Until(() => vm.IsModelReady);

        foundry.LoadedListThrows = true;
        await Screen.Until(() => !vm.IsModelReady);
        Assert.Contains("Could not check the model state: Unexpected answer", vm.StatusText);

        foundry.LoadedListThrows = false;                                // the next check still runs
        await Screen.Until(() => vm.IsModelReady);
        Assert.Contains("Ready", vm.StatusText);
    }

    [Fact]
    public async Task AModelLoadedFromTheTerminal_MakesTheAppReady()
    {
        var (vm, foundry, _, _) = Create(f => f.LoadFails = true, checkEvery: TimeSpan.FromMilliseconds(100));
        await Screen.Until(() => !vm.IsModelLoading);
        Assert.False(vm.IsModelReady);

        lock (foundry.Loaded) foundry.Loaded.Add("Phi-4-mini-instruct-generic-gpu:5");   // 'foundry model run phi-4-mini'

        await Screen.Until(() => vm.IsModelReady);
    }
}

public class MainScreenTests
{
    [Fact]
    public async Task ADocumentAndItsSummary_ReachTheChat_AndTabsSwitch()
    {
        var readiness = new Screen.Readiness();
        var activity = new ActivityTracker();
        var summarizer = new SummarizerViewModel(new DocumentIngestionPipeline(), new PromptyEngine(),
            new Screen.Summarizer((_, _) => Task.FromResult(new SummarizationResult("SUMMARY", false, 1))),
            new Screen.Picker(null), new Screen.Clipboard(), new SummarizationConfig(), readiness, activity);
        var chat = new ChatViewModel(new DocumentChatAgent(new Screen.Answers(_ => "answer")), new Screen.Questions(), readiness, activity);

        var options = new FoundryOptions();
        var picker = new ModelPickerViewModel(new FoundryLocalChatClient(options, new FoundryLocalService(options,
                new FailingCli(), new RefusingServer())), new Screen.Settings(), activity, TimeSpan.FromHours(1));
        var main = new MainViewModel(picker, summarizer, chat);
        Assert.Same(picker, main.ModelPicker);
        Assert.True(main.IsSummarizeTabSelected);                        // the app opens on the Summarize tab
        Assert.False(main.IsChatTabSelected);

        var path = Screen.TempFile("minutes.txt", "Budget is $150,000.");
        await summarizer.LoadDocumentAsync(path);
        Assert.Equal(Path.GetFileName(path), chat.DocumentName);         // document reached the chat

        await summarizer.GenerateSummaryCommand.ExecuteAsync(null);
        await Screen.Until(() => chat.SuggestedQuestions.Count == 1);    // summary reached the chat
        Assert.Equal("About SUMMARY?", chat.SuggestedQuestions[0]);

        main.SelectTabCommand.Execute("1");
        Assert.True(main.IsChatTabSelected);
        Assert.False(main.IsSummarizeTabSelected);
        main.SelectTabCommand.Execute("7");                               // unknown tab: ignored
        Assert.True(main.IsChatTabSelected);
        File.Delete(path);
    }

    private sealed class FailingCli : IFoundryCli
    {
        public Task<FoundryCliResult> RunAsync(string arguments, TimeSpan timeout, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FoundryCliResult(false, "", "not installed"));
    }

    private sealed class RefusingServer : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Connection refused");
    }
}

public class UserSettingsStoreTests
{
    [Fact]
    public void RemembersTheModel_AndSurvivesADamagedFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}", "user-settings.json");
        var store = new JsonUserSettingsStore(path);

        Assert.Null(store.Load().SelectedModelId);                       // nothing saved yet
        Assert.Null(store.Save(new UserSettings { SelectedModelId = "phi-4-mini" }));
        Assert.Equal("phi-4-mini", new JsonUserSettingsStore(path).Load().SelectedModelId);

        File.WriteAllText(path, "{damaged");
        Assert.Null(new JsonUserSettingsStore(path).Load().SelectedModelId);   // falls back to automatic choice

        Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
    }

    [Fact]
    public void AFailedSave_SaysWhy_InsteadOfThrowing()
    {
        // A file where the settings folder should be makes the folder impossible to create, on every OS.
        var blocker = Path.Combine(Path.GetTempPath(), $"blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "not a folder");
        try
        {
            var problem = new JsonUserSettingsStore(Path.Combine(blocker, "user-settings.json")).Save(new UserSettings { SelectedModelId = "phi-4-mini" });

            Assert.NotNull(problem);
            Assert.StartsWith("Could not remember the model choice", problem);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void ActivityTracker_CountsOverlappingWork()
    {
        var tracker = new ActivityTracker();
        var changes = 0;
        tracker.BusyChanged += (_, _) => changes++;

        var summary = tracker.Begin();
        var answer = tracker.Begin();
        summary.Dispose();
        Assert.True(tracker.IsBusy);                                     // the answer is still running
        answer.Dispose();
        answer.Dispose();                                                // double dispose is harmless

        Assert.False(tracker.IsBusy);
        Assert.Equal(2, changes);                                        // busy once, idle once
    }
}
