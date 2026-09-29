namespace SqlVitals.Engine.QueryStore;

/// <summary>
/// How much time one point on the Over Time charts covers (#111): one Query Store interval for
/// periods up to 2 days, an hour up to 14 days, a day beyond that, and never less than one interval.
/// Query Store counts each interval in the slot its start falls in, so none is counted twice.
/// </summary>
public static class QueryStoreTimeSlots
{
    public static readonly TimeSpan IntervalSlotsUpTo = TimeSpan.FromDays(2);
    public static readonly TimeSpan HourSlotsUpTo     = TimeSpan.FromDays(14);

    /// <summary>Query Store's default interval_length_minutes, used when the setting reads as nothing.</summary>
    public const int DefaultIntervalMinutes = 60;

    public static TimeSpan SlotLength(TimeSpan period, int intervalMinutes)
    {
        var interval = TimeSpan.FromMinutes(intervalMinutes > 0 ? intervalMinutes : DefaultIntervalMinutes);
        var slot = period <= IntervalSlotsUpTo ? interval
                 : period <= HourSlotsUpTo     ? TimeSpan.FromHours(1)
                 : TimeSpan.FromDays(1);
        return slot < interval ? interval : slot;
    }

    /// <summary>
    /// Where slots are counted from. Day slots start at midnight on this PC's clock, so a point is a
    /// local day; shorter ones at a UTC hour, where Query Store's intervals start, so a slot never
    /// splits an interval's label from its data.
    /// </summary>
    public static DateTimeOffset Origin(TimeSpan slot, TimeSpan localUtcOffset) =>
        slot >= TimeSpan.FromDays(1) && slot.Ticks % TimeSpan.TicksPerDay == 0
            ? new DateTimeOffset(2000, 1, 1, 0, 0, 0, localUtcOffset)
            : new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The slot a time falls in, counted from <paramref name="origin"/>; what the SQL's integer division gives.</summary>
    public static long SlotIndex(DateTimeOffset origin, TimeSpan slot, DateTime utc) =>
        (long)Math.Floor((DateTime.SpecifyKind(utc, DateTimeKind.Utc) - origin.UtcDateTime).TotalMinutes / slot.TotalMinutes);

    /// <summary>When slot number <paramref name="index"/> starts, in UTC.</summary>
    public static DateTime SlotStartUtc(DateTimeOffset origin, TimeSpan slot, long index) =>
        DateTime.SpecifyKind(origin.UtcDateTime + TimeSpan.FromTicks(slot.Ticks * index), DateTimeKind.Utc);

    /// <summary>Every slot that overlaps [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), by index, for drawing gaps.</summary>
    public static IEnumerable<long> SlotsBetween(DateTimeOffset origin, TimeSpan slot, DateTime fromUtc, DateTime toUtc)
    {
        var first = SlotIndex(origin, slot, fromUtc);
        var last  = SlotIndex(origin, slot, toUtc - TimeSpan.FromTicks(1));
        for (var i = first; i <= last; i++)
            yield return i;
    }
}
