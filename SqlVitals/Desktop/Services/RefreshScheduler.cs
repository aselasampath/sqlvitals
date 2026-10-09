using Microsoft.Extensions.Logging;

namespace SqlVitals.Desktop.Services;

/// <summary><see cref="IRefreshScheduler"/> on a <see cref="TimeProvider"/>'s timer, each tick posted to the UI thread.</summary>
/// <param name="log">Where a tick that throws is written; <see cref="AppLog"/>'s when null (tests).</param>
public sealed class RefreshScheduler(TimeProvider time, IUiDispatcher ui, ILogger<RefreshScheduler>? log = null) : IRefreshScheduler
{
    private readonly ILogger _log = log ?? AppLog.For<RefreshScheduler>();

    public IRefreshSchedule Every(TimeSpan interval, Func<Task> tick)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        return new Schedule(time, ui, interval, tick, _log);
    }

    private sealed class Schedule : IRefreshSchedule
    {
        private readonly TimeProvider _time;
        private readonly TimeSpan _interval;
        private readonly IUiDispatcher _ui;
        private readonly Func<Task> _tick;
        private readonly ITimer _timer;
        private readonly ILogger _log;
        private volatile bool _disposed;
        private bool _running;

        // When the timer fires next, in UTC ticks: set on the timer's thread, read on the UI thread.
        private long _due;

        public Schedule(TimeProvider time, IUiDispatcher ui, TimeSpan interval, Func<Task> tick, ILogger log)
        {
            _time     = time;
            _interval = interval;
            _ui       = ui;
            _tick     = tick;
            _log      = log;
            _due      = (time.GetUtcNow() + interval).UtcTicks;
            _timer    = time.CreateTimer(_ => Fired(), null, interval, interval);
        }

        public TimeSpan Remaining
        {
            get
            {
                var left = new TimeSpan(Interlocked.Read(ref _due) - _time.GetUtcNow().UtcTicks);
                return left > TimeSpan.Zero ? left : TimeSpan.Zero;
            }
        }

        // On the timer's thread: the next one is an interval away, and this one runs on the UI thread.
        private void Fired()
        {
            Interlocked.Exchange(ref _due, (_time.GetUtcNow() + _interval).UtcTicks);
            _ui.Post(() => _ = FireAsync());
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
