using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FoundrySummarizer.Core.Routing;

namespace FoundrySummarizer.Wpf.ViewModels;

/// <summary>
/// The window: a Summarize tab and a Chat tab for follow-up questions, plus the Foundry Local status.
/// Loading a document starts a new chat about it; a new summary is handed to the chat as extra context.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    /// <summary>Index of the Summarize tab.</summary>
    public const int SummarizeTab = 0;

    /// <summary>Index of the Chat tab.</summary>
    public const int ChatTab = 1;

    private readonly FoundryLocalChatClient _modelClient;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSummarizeTabSelected), nameof(IsChatTabSelected))]
    private int _selectedTabIndex = SummarizeTab;

    [ObservableProperty]
    private bool _isModelReady;

    [ObservableProperty]
    private string _modelStatusText = "Foundry Local: checking...";

    /// <summary>Bound to the Summarize tab button.</summary>
    public bool IsSummarizeTabSelected => SelectedTabIndex == SummarizeTab;

    /// <summary>Bound to the Chat tab button.</summary>
    public bool IsChatTabSelected => SelectedTabIndex == ChatTab;

    /// <summary>The Summarize tab.</summary>
    public SummarizerViewModel SummarizerVm { get; }

    /// <summary>The Chat tab.</summary>
    public ChatViewModel ChatVm { get; }

    /// <param name="modelClient">Reports the Foundry Local status.</param>
    /// <param name="summarizerVm">The Summarize tab.</param>
    /// <param name="chatVm">The Chat tab.</param>
    public MainViewModel(FoundryLocalChatClient modelClient, SummarizerViewModel summarizerVm, ChatViewModel chatVm)
    {
        _modelClient = modelClient;
        SummarizerVm = summarizerVm;
        ChatVm = chatVm;

        SummarizerVm.DocumentLoaded += ChatVm.StartSession;
        SummarizerVm.SummaryGenerated += summary =>
        {
            ChatVm.UpdateSummary(summary);
            _ = RefreshModelStatusAsync();   // the model was just loaded or switched; show which one answered
        };

        _ = RefreshModelStatusAsync();
    }

    /// <summary>Switches tabs; the parameter is the tab index as text (from XAML).</summary>
    [RelayCommand]
    private void SelectTab(string index)
    {
        if (int.TryParse(index, out var tab) && tab is SummarizeTab or ChatTab)
        {
            SelectedTabIndex = tab;
        }
    }

    /// <summary>Checks whether Foundry Local is reachable and which model it will use. Does not load a model.</summary>
    [RelayCommand]
    private async Task RefreshModelStatusAsync()
    {
        ModelStatusText = "Foundry Local: checking...";
        var status = await _modelClient.GetStatusAsync();
        IsModelReady = status.IsAvailable;
        ModelStatusText = status.IsAvailable
            ? $"Foundry Local ready · {status.ModelId}"
            : $"⚠️ Foundry Local not reachable: {status.Problem}";
    }
}
