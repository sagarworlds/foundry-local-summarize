using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FoundrySummarizer.Core.Routing;
using FoundrySummarizer.Wpf.Services;

namespace FoundrySummarizer.Wpf.ViewModels;

/// <summary>
/// The window: a Summarize tab, a Chat tab for follow-up questions, and the Foundry Local model picker.
/// Loading a document starts a new chat about it; a new summary is handed to the chat as extra context.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    /// <summary>Index of the Summarize tab.</summary>
    public const int SummarizeTab = 0;

    /// <summary>Index of the Chat tab.</summary>
    public const int ChatTab = 1;

    private readonly FoundryLocalChatClient _modelClient;
    private readonly IUserSettingsStore _settingsStore;

    // True while the list is being filled in code, so that doesn't count as the user picking a model.
    private bool _isUpdatingModelList;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSummarizeTabSelected), nameof(IsChatTabSelected))]
    private int _selectedTabIndex = SummarizeTab;

    [ObservableProperty]
    private bool _isModelReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeModel))]
    [NotifyCanExecuteChangedFor(nameof(RefreshModelsCommand))]
    private bool _isModelBusy;

    [ObservableProperty]
    private string _modelStatusText = "Connecting to Foundry Local...";

    [ObservableProperty]
    private LocalModelInfo? _selectedModel;

    /// <summary>Chat models downloaded on this machine, loaded ones first.</summary>
    public ObservableCollection<LocalModelInfo> AvailableModels { get; } = new();

    /// <summary>The model list is enabled only when no refresh or load is in progress.</summary>
    public bool CanChangeModel => !IsModelBusy;

    /// <summary>Bound to the Summarize tab button.</summary>
    public bool IsSummarizeTabSelected => SelectedTabIndex == SummarizeTab;

    /// <summary>Bound to the Chat tab button.</summary>
    public bool IsChatTabSelected => SelectedTabIndex == ChatTab;

    /// <summary>The Summarize tab.</summary>
    public SummarizerViewModel SummarizerVm { get; }

    /// <summary>The Chat tab.</summary>
    public ChatViewModel ChatVm { get; }

    /// <param name="modelClient">Lists, selects and loads Foundry Local models.</param>
    /// <param name="settingsStore">Remembers the chosen model between runs.</param>
    /// <param name="summarizerVm">The Summarize tab.</param>
    /// <param name="chatVm">The Chat tab.</param>
    public MainViewModel(FoundryLocalChatClient modelClient, IUserSettingsStore settingsStore, SummarizerViewModel summarizerVm, ChatViewModel chatVm)
    {
        _modelClient = modelClient;
        _settingsStore = settingsStore;
        SummarizerVm = summarizerVm;
        ChatVm = chatVm;

        SummarizerVm.DocumentLoaded += ChatVm.StartSession;
        SummarizerVm.SummaryGenerated += ChatVm.UpdateSummary;

        _modelClient.SelectModel(_settingsStore.Load().SelectedModelId);
        _ = RefreshModelsAsync();
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

    /// <summary>
    /// Finds Foundry Local (starting it if needed), lists the downloaded models and shows which one will be used.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeModel))]
    private async Task RefreshModelsAsync()
    {
        IsModelBusy = true;
        ModelStatusText = "Connecting to Foundry Local (starting it if needed)...";
        try
        {
            var list = await _modelClient.ListModelsAsync();
            if (list.Models.Count == 0)
            {
                ShowModels(list.Models, selectedId: null);
                IsModelReady = false;
                ModelStatusText = $"⚠️ {list.Problem}";
                return;
            }

            // A remembered model that has since been deleted from the cache would fail on every request.
            var chosen = _modelClient.UserSelectedModelId;
            if (chosen is not null && !list.Models.Any(m => string.Equals(m.Id, chosen, StringComparison.OrdinalIgnoreCase)))
            {
                _modelClient.SelectModel(null);
                RememberModel(null);
            }

            // Resolves the automatic choice (without loading it) so the list can show it.
            var status = await _modelClient.GetStatusAsync();
            ShowModels(list.Models, status.ModelId);
            ShowStatus(status);
        }
        finally
        {
            IsModelBusy = false;
        }
    }

    partial void OnSelectedModelChanged(LocalModelInfo? value)
    {
        if (_isUpdatingModelList || value is null) return;

        _modelClient.SelectModel(value.Id);
        RememberModel(value.Id);
        _ = LoadSelectedModelAsync(value.Id);
    }

    /// <summary>Loads the chosen model right away, so the first summary does not also wait for the load.</summary>
    private async Task LoadSelectedModelAsync(string modelId)
    {
        IsModelBusy = true;
        ModelStatusText = $"Loading {modelId}... the first load can take a minute.";
        try
        {
            var status = await _modelClient.LoadActiveModelAsync();
            ShowStatus(status);
            if (status.IsAvailable)
            {
                // Mark it as loaded without re-listing: the load just confirmed it.
                ShowModels(AvailableModels.Select(m => m.Id == modelId ? m with { IsLoaded = true } : m).ToList(), modelId);
            }
        }
        finally
        {
            IsModelBusy = false;
        }
    }

    private void ShowModels(IReadOnlyList<LocalModelInfo> models, string? selectedId)
    {
        _isUpdatingModelList = true;
        try
        {
            AvailableModels.Clear();
            foreach (var model in models) AvailableModels.Add(model);

            SelectedModel = AvailableModels.FirstOrDefault(m => string.Equals(m.Id, selectedId, StringComparison.OrdinalIgnoreCase));
            if (SelectedModel is null && selectedId is not null)
            {
                // The configured fallback model may not be in the downloaded list; still show what will be used.
                var fallback = new LocalModelInfo(selectedId, IsLoaded: false);
                AvailableModels.Add(fallback);
                SelectedModel = fallback;
            }
        }
        finally
        {
            _isUpdatingModelList = false;
        }
    }

    private void ShowStatus(LocalModelStatus status)
    {
        IsModelReady = status.IsAvailable;
        ModelStatusText = status.IsAvailable
            ? $"Foundry Local ready{(_modelClient.UserSelectedModelId is null ? " (model chosen automatically)" : string.Empty)}"
            : $"⚠️ {status.Problem}";
    }

    private void RememberModel(string? modelId)
    {
        if (_settingsStore.Save(new UserSettings { SelectedModelId = modelId }) is { } problem)
        {
            ModelStatusText = $"⚠️ {problem}";
        }
    }
}
