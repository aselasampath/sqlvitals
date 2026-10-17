using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Tests.Fakes;

/// <summary>
/// An <see cref="IUiDispatcher"/> for tests without a UI thread (#182): <see cref="InvokeAsync"/> runs
/// at once, and <see cref="Post"/> queues until <see cref="RunPosted"/>, so a test sees the order.
/// </summary>
internal sealed class FakeUiDispatcher : IUiDispatcher
{
    private readonly Queue<Action> _posted = new();

    public bool IsOnUiThread => true;

    /// <summary>How many posted actions wait for <see cref="RunPosted"/>.</summary>
    public int Pending => _posted.Count;

    public void Post(Action action) => _posted.Enqueue(action);

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public void Invoke(Action action, TimeSpan timeout) => action();

    /// <summary>Runs what was posted, in order, including what those post.</summary>
    public void RunPosted()
    {
        while (_posted.TryDequeue(out var action))
            action();
    }
}
