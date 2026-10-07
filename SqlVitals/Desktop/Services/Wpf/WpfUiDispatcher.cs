using System.Windows.Threading;

namespace SqlVitals.Desktop.Services.Wpf;

/// <summary><see cref="IUiDispatcher"/> over the dispatcher of the thread the app's windows live on.</summary>
internal sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public bool IsOnUiThread => dispatcher.CheckAccess();

    public void Post(Action action) => dispatcher.BeginInvoke(action);

    public Task InvokeAsync(Action action)
    {
        if (dispatcher.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }
        return dispatcher.InvokeAsync(action).Task;
    }
}
