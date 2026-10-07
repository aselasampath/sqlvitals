using System.Collections.Concurrent;
using Microsoft.Extensions.Time.Testing;

namespace SqlVitals.Engine.Tests.ChatNotifications;

/// <summary>
/// A fake clock whose timers fire straight away, moving the clock on by what they waited for. Code
/// that paces or backs off runs to the end at once, and the times it did things at can be read
/// back from the clock, so no test waits in real time (#122). For a clock that stays put until
/// the test moves it, use <see cref="FakeTimeProvider"/> itself.
/// </summary>
internal sealed class SteppingTime() : FakeTimeProvider(new DateTimeOffset(2026, 9, 30, 10, 42, 0, TimeSpan.Zero))
{
    public static readonly DateTimeOffset T0 = new(2026, 9, 30, 10, 42, 0, TimeSpan.Zero);

    /// <summary>Every wait asked for, in order.</summary>
    public ConcurrentQueue<TimeSpan> Waits { get; } = new();

    /// <summary>How long after <see cref="T0"/> it is now.</summary>
    public TimeSpan Elapsed => GetUtcNow() - T0;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
        {
            Waits.Enqueue(dueTime);
            // Not inline: the caller (Task.Delay) hasn't kept the timer yet.
            ThreadPool.QueueUserWorkItem(_ => Advance(dueTime));
        }
        return timer;
    }
}
