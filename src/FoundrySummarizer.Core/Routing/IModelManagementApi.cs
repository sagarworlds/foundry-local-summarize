namespace FoundrySummarizer.Core.Routing;

/// <summary>
/// Model management for one kind of local server. Foundry Local changed its web API between versions (0.x runs
/// "foundry service" with /openai/* routes, 1.x+ runs "foundry server" with /models/* routes), and generic
/// OpenAI-compatible servers such as Ollama have no model management at all. Each has its own implementation,
/// so <see cref="FoundryLocalService"/> works the same against all of them.
/// </summary>
/// <remarks>
/// A "model name" is what the user picks and what load/unload take (a catalog id in 0.x, an alias such as
/// "phi-4-mini" in 1.x+). The chat request may need a different id (<see cref="ChatModelId"/>).
/// </remarks>
public interface IModelManagementApi
{
    /// <summary>Short description for messages, e.g. "Foundry Local (foundry server)".</summary>
    string DisplayName { get; }

    /// <summary>False for servers that load models on demand (nothing to load, unload or confirm).</summary>
    bool SupportsLoading { get; }

    /// <summary>Names of the chat models downloaded on this machine.</summary>
    Task<ModelIdListing> ListDownloadedAsync(CancellationToken cancellationToken);

    /// <summary>Models in memory, as the server reports them.</summary>
    Task<ModelIdListing> ListLoadedAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Turns any spelling of a model (configured id, preference alias, saved choice, id with or without version)
    /// into the model name this server's load route accepts; unknown names are returned unchanged.
    /// </summary>
    Task<string> NormalizeAsync(string name, CancellationToken cancellationToken);

    /// <summary>True when the model <paramref name="name"/> is among <paramref name="loaded"/>.</summary>
    bool IsLoaded(IReadOnlyList<string> loaded, string name);

    /// <summary>The id to put in chat requests for <paramref name="name"/>, given what is loaded.</summary>
    string ChatModelId(IReadOnlyList<string> loaded, string name);

    /// <summary>Loads <paramref name="name"/> into memory.</summary>
    /// <returns>Null on success; otherwise what went wrong and how to fix it.</returns>
    Task<string?> LoadAsync(string name, CancellationToken cancellationToken);

    /// <summary>Releases <paramref name="name"/> from memory.</summary>
    /// <returns>Null on success or when it was not loaded; otherwise why unloading failed.</returns>
    Task<string?> UnloadAsync(string name, CancellationToken cancellationToken);
}
