namespace FoundrySummarizer.Presentation.Services;

/// <summary>
/// Tracks model work in progress (a summary or a chat answer), so the model is not switched or unloaded
/// while a request is using it.
/// </summary>
public interface IActivityTracker
{
    /// <summary>True while at least one piece of model work is running.</summary>
    bool IsBusy { get; }

    /// <summary>Raised whenever <see cref="IsBusy"/> changes.</summary>
    event EventHandler? BusyChanged;

    /// <summary>Marks work as started; dispose the result when it ends.</summary>
    IDisposable Begin();
}

/// <summary>Counts overlapping pieces of work (e.g. a summary while a chat answer is still running).</summary>
public sealed class ActivityTracker : IActivityTracker
{
    private int _running;

    /// <inheritdoc />
    public bool IsBusy => _running > 0;

    /// <inheritdoc />
    public event EventHandler? BusyChanged;

    /// <inheritdoc />
    public IDisposable Begin()
    {
        if (_running++ == 0) BusyChanged?.Invoke(this, EventArgs.Empty);
        return new Scope(this);
    }

    private void End()
    {
        if (--_running == 0) BusyChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Scope : IDisposable
    {
        private ActivityTracker? _owner;

        public Scope(ActivityTracker owner) => _owner = owner;

        // Idempotent, so a double dispose cannot drive the counter negative.
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.End();
    }
}
