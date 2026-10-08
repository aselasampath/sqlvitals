namespace SqlVitals.Desktop.Services;

/// <summary><see cref="IRefreshScheduler"/> on a <see cref="TimeProvider"/>'s timer, each tick posted to the UI thread.</summary>
public sealed class RefreshScheduler(TimeProvider time, IUiDispatcher ui) : IRefreshScheduler
{
    public IDisposable Every(TimeSpan interval, Func<Task> tick)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        return new Schedule(time, ui, interval, tick);
    }

    private sealed class Schedule : IDisposable
    {
        private readonly IUiDispatcher _ui;
        private readonly Func<Task> _tick;
        private readonly ITimer _timer;
        private volatile bool _disposed;
        private bool _running;

        public Schedule(TimeProvider time, IUiDispatcher ui, TimeSpan interval, Func<Task> tick)
        {
            _ui    = ui;
            _tick  = tick;
            _timer = time.CreateTimer(_ => _ui.Post(() => _ = FireAsync()), null, interval, interval);
        }

        // On the UI thread. A tick's failure is the tick's to report: the schedule keeps going.
        private async Task FireAsync()
        {
            if (_disposed || _running)
                return;
            _running = true;
            try
            {
                await _tick();
            }
            catch (Exception ex)
            {
                AppLog.Error("Timed refresh failed", ex);
            }
            finally
            {
                _running = false;
            }
        }

        public void Dispose()
        {
            _disposed = true;
            _timer.Dispose();
        }
    }
}
