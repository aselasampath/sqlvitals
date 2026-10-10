using System.Collections.Concurrent;

namespace SqlVitals.Collector;

/// <summary>
/// One thread that runs the work posted to it in order (#172): what the app's UI thread is for
/// its <c>MonitoringManager</c> and chat channels. The collector service has no window, but the
/// chat service still expects one thread at a time, so everything that touches it runs here.
/// </summary>
internal sealed class SerialContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Work, object? State)> _work = new();
    private readonly Thread                _thread;
    private readonly Action<string, Exception> _onError;
    private volatile bool                  _closed;

    /// <param name="onError">Gets a failure of a work item; the thread carries on with the next one.</param>
    public SerialContext(string name, Action<string, Exception> onError)
    {
        _onError = onError;
        _thread  = new Thread(Run) { IsBackground = true, Name = name };
        _thread.Start();
    }

    /// <summary>Whether the caller is on this context's thread.</summary>
    public bool IsCurrent => Thread.CurrentThread == _thread;

    public override void Post(SendOrPostCallback d, object? state)
    {
        // After Dispose there is nothing left to run it: dropped, as the app drops what it posts
        // to a window that has closed. A chat queue's worker may still report after that.
        if (_closed)
            return;
        try { _work.Add((d, state)); }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { /* closed meanwhile */ }
    }

    public override void Send(SendOrPostCallback d, object? state) =>
        throw new NotSupportedException("Post to the collector's thread; it never blocks a caller.");

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>Runs <paramref name="work"/> on this thread; completes when it has run, with its failure.</summary>
    public Task RunAsync(Action work)
    {
        if (IsCurrent)
        {
            work();
            return Task.CompletedTask;
        }
        if (_closed)
            return Task.FromException(new ObjectDisposedException(nameof(SerialContext)));
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ =>
        {
            try { work(); done.TrySetResult(); }
            catch (Exception ex) { done.TrySetException(ex); }
        }, null);
        // Closed between the check and the post: it will never run.
        if (_closed)
            done.TrySetException(new ObjectDisposedException(nameof(SerialContext)));
        return done.Task;
    }

    private void Run()
    {
        SetSynchronizationContext(this);
        foreach (var (work, state) in _work.GetConsumingEnumerable())
        {
            try { work(state); }
            catch (Exception ex) { _onError("Collector: a background task failed", ex); }
        }
    }

    /// <summary>Runs what's already posted, then stops the thread.</summary>
    public void Dispose()
    {
        if (_closed)
            return;
        _closed = true;
        _work.CompleteAdding();
        // A work item stuck past the wait keeps its collection: the thread is a background one.
        if (!IsCurrent && _thread.Join(TimeSpan.FromSeconds(10)))
            _work.Dispose();
    }
}
