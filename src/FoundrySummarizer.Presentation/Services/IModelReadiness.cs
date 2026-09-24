namespace FoundrySummarizer.Presentation.Services;

/// <summary>Whether a model is loaded and can answer. Screens use it to enable or disable their actions.</summary>
public interface IModelReadiness
{
    /// <summary>True when a model is loaded in Foundry Local and ready for requests.</summary>
    bool IsModelReady { get; }

    /// <summary>True while a model is being loaded or switched.</summary>
    bool IsModelLoading { get; }

    /// <summary>Raised on the UI thread whenever <see cref="IsModelReady"/> or <see cref="IsModelLoading"/> changes.</summary>
    event EventHandler? ReadinessChanged;
}
