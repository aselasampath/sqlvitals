using Microsoft.Extensions.Time.Testing;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// A fake clock whose timers fire straight away, moving the clock on by what they waited for, so
/// the chat queues' pacing and retries (#122) run to the end without waiting in real time. The
/// same as the Engine tests' helper.
/// </summary>
internal sealed class SteppingTime() : FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 10, 42, 0, TimeSpan.Zero))
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
            // Not inline: the caller (Task.Delay) hasn't kept the timer yet.
            ThreadPool.QueueUserWorkItem(_ => Advance(dueTime));
        return timer;
    }
}
