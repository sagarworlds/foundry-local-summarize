using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace FoundrySummarizer.Wpf.ViewModels;

/// <summary>
/// The window: the model picker, a Summarize tab and a Chat tab for follow-up questions.
/// Loading a document starts a new chat about it; a new summary is handed to the chat as extra context.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    /// <summary>Index of the Summarize tab.</summary>
    public const int SummarizeTab = 0;

    /// <summary>Index of the Chat tab.</summary>
    public const int ChatTab = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSummarizeTabSelected), nameof(IsChatTabSelected))]
    private int _selectedTabIndex = SummarizeTab;

    /// <summary>Bound to the Summarize tab button.</summary>
    public bool IsSummarizeTabSelected => SelectedTabIndex == SummarizeTab;

    /// <summary>Bound to the Chat tab button.</summary>
    public bool IsChatTabSelected => SelectedTabIndex == ChatTab;

    /// <summary>The model dropdown and loading state.</summary>
    public ModelPickerViewModel ModelPicker { get; }

    /// <summary>The Summarize tab.</summary>
    public SummarizerViewModel SummarizerVm { get; }

    /// <summary>The Chat tab.</summary>
    public ChatViewModel ChatVm { get; }

    /// <param name="modelPicker">The model dropdown.</param>
    /// <param name="summarizerVm">The Summarize tab.</param>
    /// <param name="chatVm">The Chat tab.</param>
    public MainViewModel(ModelPickerViewModel modelPicker, SummarizerViewModel summarizerVm, ChatViewModel chatVm)
    {
        ModelPicker = modelPicker;
        SummarizerVm = summarizerVm;
        ChatVm = chatVm;

        SummarizerVm.DocumentLoaded += ChatVm.StartSession;
        SummarizerVm.SummaryGenerated += ChatVm.UpdateSummary;
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
}
