namespace SqlVitals.Desktop.Services;

/// <summary>
/// The UI thread (#182), for the few places that hear of a change on another thread and must
/// update what is bound: a settings change saved by monitoring, a chat queue's result.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Whether the caller is on the UI thread, where bound properties may be set.</summary>
    bool IsOnUiThread { get; }

    /// <summary>Runs <paramref name="action"/> on the UI thread later, without waiting.</summary>
    void Post(Action action);

    /// <summary>Runs <paramref name="action"/> on the UI thread: at once when on it, otherwise when it gets there.</summary>
    Task InvokeAsync(Action action);

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread ahead of anything waiting there, and waits for
    /// it, but not past <paramref name="timeout"/>: one that hasn't run by then doesn't run.
    /// </summary>
    void Invoke(Action action, TimeSpan timeout);
}
