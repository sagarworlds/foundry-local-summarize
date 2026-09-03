using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FoundrySummarizer.Core.Grounding;

namespace FoundrySummarizer.Wpf.ViewModels;

public partial class GroundingViewModel : ObservableObject
{
    private readonly IVectorGroundingService _groundingService;

    [ObservableProperty]
    private string _searchQuery = "hardware budget $150,000 for Phase 2";

    [ObservableProperty]
    private string _searchStatus = "Search policies to verify semantic similarity scores.";

    [ObservableProperty]
    private string _newTitle = string.Empty;

    [ObservableProperty]
    private string _newCategory = "General Policy";

    [ObservableProperty]
    private string _newContent = string.Empty;

    public ObservableCollection<GroundingRecord> IndexedPolicies { get; } = new();
    public ObservableCollection<GroundingSearchResult> SearchResults { get; } = new();

    public GroundingViewModel(IVectorGroundingService groundingService)
    {
        _groundingService = groundingService;
        RefreshPolicies();
    }

    public void RefreshPolicies()
    {
        IndexedPolicies.Clear();
        foreach (var p in _groundingService.IndexedPolicies)
        {
            IndexedPolicies.Add(p);
        }
    }

    [RelayCommand]
    public async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return;

        SearchStatus = "Computing 384-dimensional cosine vector similarity...";
        SearchResults.Clear();

        var results = await _groundingService.SearchAsync(SearchQuery, topK: 5, minScore: 0.15);
        foreach (var res in results)
        {
            SearchResults.Add(res);
        }

        SearchStatus = $"Found {results.Count} matching policy records in Microsoft.Extensions.VectorData.";
    }

    [RelayCommand]
    public async Task AddPolicyAsync()
    {
        if (string.IsNullOrWhiteSpace(NewTitle) || string.IsNullOrWhiteSpace(NewContent))
        {
            SearchStatus = "Please provide both title and content for the new policy.";
            return;
        }

        await _groundingService.IndexPolicyAsync(NewTitle, NewContent, NewCategory, "User Added");
        RefreshPolicies();

        SearchStatus = $"Successfully indexed policy '{NewTitle}' into vector store.";
        NewTitle = string.Empty;
        NewContent = string.Empty;
    }
}
