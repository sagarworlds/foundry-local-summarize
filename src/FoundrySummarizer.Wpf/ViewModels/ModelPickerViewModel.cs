using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FoundrySummarizer.Core.Routing;
using FoundrySummarizer.Wpf.Services;

namespace FoundrySummarizer.Wpf.ViewModels;

/// <summary>
/// The model dropdown: lists the models downloaded on this machine, loads the chosen one (unloading the previous
/// one) and reports whether a model is ready. Nothing can be summarized or asked until a model is loaded.
/// </summary>
public partial class ModelPickerViewModel : ObservableObject, IModelReadiness
{
    private readonly FoundryLocalChatClient _modelClient;
    private readonly IUserSettingsStore _settingsStore;
    private readonly IActivityTracker _activity;

    // True while the list is being filled in code, so that doesn't count as the user picking a model.
    private bool _isUpdatingList;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowNotReadyBanner))]
    private bool _isModelReady;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChangeModel), nameof(ShowNotReadyBanner))]
    [NotifyCanExecuteChangedFor(nameof(RefreshModelsCommand))]
    private bool _isModelLoading;

    [ObservableProperty]
    private string _loadingText = string.Empty;

    [ObservableProperty]
    private string _statusText = "Connecting to Foundry Local...";

    [ObservableProperty]
    private LocalModelInfo? _selectedModel;

    /// <inheritdoc />
    public event EventHandler? ReadinessChanged;

    /// <summary>Chat models downloaded on this machine, loaded ones first.</summary>
    public ObservableCollection<LocalModelInfo> AvailableModels { get; } = new();

    /// <summary>The model can be changed only when nothing is loading and no summary or answer is running.</summary>
    public bool CanChangeModel => !IsModelLoading && !_activity.IsBusy;

    /// <summary>Shown above the tabs when no model is loaded and none is loading.</summary>
    public bool ShowNotReadyBanner => !IsModelReady && !IsModelLoading;

    /// <param name="modelClient">Lists, loads and unloads Foundry Local models.</param>
    /// <param name="settingsStore">Remembers the chosen model between runs.</param>
    /// <param name="activity">Tells whether a summary or answer is running (the model is locked meanwhile).</param>
    public ModelPickerViewModel(FoundryLocalChatClient modelClient, IUserSettingsStore settingsStore, IActivityTracker activity)
    {
        _modelClient = modelClient;
        _settingsStore = settingsStore;
        _activity = activity;
        _activity.BusyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CanChangeModel));
            RefreshModelsCommand.NotifyCanExecuteChanged();
        };

        _modelClient.SelectModel(_settingsStore.Load().SelectedModelId);
        _ = RefreshModelsAsync();
    }

    partial void OnIsModelReadyChanged(bool value) => ReadinessChanged?.Invoke(this, EventArgs.Empty);

    partial void OnIsModelLoadingChanged(bool value) => ReadinessChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Finds Foundry Local (starting it if needed), lists the downloaded models and makes sure the chosen model
    /// is loaded. Runs at startup and from the ↻ button.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanChangeModel))]
    private async Task RefreshModelsAsync()
    {
        BeginLoading("Connecting to Foundry Local (starting it if needed)...");
        try
        {
            var list = await _modelClient.ListModelsAsync();
            if (list.Models.Count == 0)
            {
                ShowModels(list.Models, selectedId: null);
                SetNotReady(list.Problem ?? "No models found.");
                return;
            }

            // A remembered model that has since been deleted would fail on every request.
            var chosen = _modelClient.UserSelectedModelId;
            if (chosen is not null && !list.Models.Any(m => FoundryLocalService.SameModel(m.Id, chosen)))
            {
                _modelClient.SelectModel(null);
                RememberModel(null);
            }

            // Resolve which model will be used (without loading) so the dropdown can show it while it loads.
            ShowModels(list.Models, (await _modelClient.GetStatusAsync()).ModelId);
            LoadingText = $"Loading {_modelClient.ActiveModelId}...\nThe first load can take a minute.";

            var status = await _modelClient.LoadActiveModelAsync();
            ApplyStatus(status);
        }
        finally
        {
            IsModelLoading = false;
        }
    }

    partial void OnSelectedModelChanged(LocalModelInfo? value)
    {
        if (_isUpdatingList || value is null) return;
        _ = SwitchModelAsync(value.Id);
    }

    /// <summary>Unloads the current model and loads <paramref name="modelId"/>; every action waits meanwhile.</summary>
    private async Task SwitchModelAsync(string modelId)
    {
        BeginLoading($"Loading {modelId}...\nThe first load can take a minute.");
        try
        {
            var saveProblem = RememberModel(modelId);
            var previous = _modelClient.ActiveModelId;
            var result = await _modelClient.SwitchModelAsync(modelId);
            ApplyStatus(result.Status, unloadedModelId: result.UnloadProblem is null ? previous : null);
            var warnings = new[] { result.UnloadProblem, saveProblem }.OfType<string>().ToList();
            if (result.Status.IsAvailable && warnings.Count > 0)
            {
                StatusText = $"Ready. Note: {string.Join(" ", warnings)}";
            }
        }
        finally
        {
            IsModelLoading = false;
        }
    }

    private void BeginLoading(string text)
    {
        LoadingText = text;
        StatusText = text.Split('\n')[0];
        IsModelReady = false;
        IsModelLoading = true;
    }

    /// <param name="status">State after loading.</param>
    /// <param name="unloadedModelId">A model that was just unloaded, so its "in memory" mark is cleared.</param>
    private void ApplyStatus(LocalModelStatus status, string? unloadedModelId = null)
    {
        if (!status.IsAvailable)
        {
            SetNotReady(status.Problem ?? "The model could not be loaded.");
            return;
        }

        // Update the "in memory" marks from what just happened, without asking the service again.
        ShowModels(AvailableModels
            .Select(m => m with
            {
                IsLoaded = FoundryLocalService.SameModel(m.Id, status.ModelId)
                           || (m.IsLoaded && (unloadedModelId is null || !FoundryLocalService.SameModel(m.Id, unloadedModelId)))
            })
            .ToList(), status.ModelId);
        IsModelReady = true;
        StatusText = _modelClient.UserSelectedModelId is null ? "Ready (model chosen automatically)" : "Ready";
    }

    private void SetNotReady(string problem)
    {
        IsModelReady = false;
        StatusText = $"⚠️ {problem}";
    }

    private void ShowModels(IReadOnlyList<LocalModelInfo> models, string? selectedId)
    {
        _isUpdatingList = true;
        try
        {
            AvailableModels.Clear();
            foreach (var model in models) AvailableModels.Add(model);

            SelectedModel = selectedId is null
                ? null
                : AvailableModels.FirstOrDefault(m => FoundryLocalService.SameModel(m.Id, selectedId));
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
            _isUpdatingList = false;
        }
    }

    /// <returns>Null when saved; otherwise why the choice will not be remembered.</returns>
    private string? RememberModel(string? modelId) =>
        _settingsStore.Save(new UserSettings { SelectedModelId = modelId });
}
