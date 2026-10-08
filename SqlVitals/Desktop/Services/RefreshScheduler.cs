using Microsoft.Extensions.Logging;

namespace SqlVitals.Desktop.Services;

/// <summary><see cref="IRefreshScheduler"/> on a <see cref="TimeProvider"/>'s timer, each tick posted to the UI thread.</summary>
/// <param name="log">Where a tick that throws is written; <see cref="AppLog"/>'s when null (tests).</param>
public sealed class RefreshScheduler(TimeProvider time, IUiDispatcher ui, ILogger<RefreshScheduler>? log = null) : IRefreshScheduler
{
    private readonly ILogger _log = log ?? AppLog.For<RefreshScheduler>();

    public IDisposable Every(TimeSpan interval, Func<Task> tick)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        return new Schedule(time, ui, interval, tick, _log);
    }

    private sealed class Schedule : IDisposable
    {
        private readonly IUiDispatcher _ui;
        private readonly Func<Task> _tick;
        private readonly ITimer _timer;
        private readonly ILogger _log;
        private volatile bool _disposed;
        private bool _running;

        public Schedule(TimeProvider time, IUiDispatcher ui, TimeSpan interval, Func<Task> tick, ILogger log)
        {
            _ui    = ui;
            _tick  = tick;
            _log   = log;
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
                _log.Error("Timed refresh failed", ex);
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
