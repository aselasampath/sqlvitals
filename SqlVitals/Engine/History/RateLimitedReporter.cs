namespace SqlVitals.Engine.History;

/// <summary>
/// Passes errors on at most once per <see cref="Interval"/>, noting how many were held back,
/// so a full disk or an unreachable server doesn't write a log line every few seconds.
/// Thread-safe, and never throws: reporting must not become a new failure.
/// </summary>
public sealed class RateLimitedReporter(Action<string, Exception> report, TimeProvider? time = null)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object       _gate = new();
    private DateTimeOffset        _nextAllowed = DateTimeOffset.MinValue;
    private int                   _suppressed;

    public void Report(string message, Exception ex)
    {
        int suppressed;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (now < _nextAllowed)
            {
                _suppressed++;
                return;
            }
            suppressed   = _suppressed;
            _suppressed  = 0;
            _nextAllowed = now + Interval;
        }

        try
        {
            report(suppressed == 0 ? message : $"{message} ({suppressed} similar error(s) not logged)", ex);
        }
        catch
        {
            // Nowhere left to report to.
        }
    }
}
