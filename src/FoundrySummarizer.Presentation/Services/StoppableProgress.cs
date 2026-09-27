namespace FoundrySummarizer.Presentation.Services;

/// <summary>
/// A <see cref="Progress{T}"/> that can be stopped: once <see cref="Stop"/> returns, no report is handled any more,
/// not even one that was already on its way. <see cref="Progress{T}"/> hands reports to the UI thread later, so
/// without this a late "Reading part 3…" or partial answer could overwrite the final status or a finished answer.
/// </summary>
/// <typeparam name="T">The report type.</typeparam>
public sealed class StoppableProgress<T> : IProgress<T>
{
    // Handling a report and stopping exclude each other, so a report is either handled completely before Stop
    // returns, or not at all.
    private readonly object _gate = new();
    private readonly IProgress<T> _inner;
    private bool _stopped;

    /// <param name="handler">Handles each report, on the thread that created this object (the UI thread in the app).</param>
    public StoppableProgress(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _inner = new Progress<T>(value =>
        {
            lock (_gate)
            {
                if (!_stopped) handler(value);
            }
        });
    }

    /// <inheritdoc />
    public void Report(T value) => _inner.Report(value);

    /// <summary>Stops handling reports; any report still pending is dropped.</summary>
    public void Stop()
    {
        lock (_gate) _stopped = true;
    }
}
