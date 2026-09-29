using SqlVitals.Engine.QueryStore;

namespace SqlVitals.Engine.Tests.QueryStore;

public class QueryStoreTimeSlotsTests
{
    [Theory]
    [InlineData(1,      60, 60)]         // last hour: one interval
    [InlineData(24,     60, 60)]         // last 24 hours
    [InlineData(48,     15, 15)]         // 2 days: still one interval
    [InlineData(49,     15, 60)]         // just over 2 days: an hour
    [InlineData(7 * 24, 60, 60)]         // last 7 days
    [InlineData(14 * 24, 5, 60)]         // 14 days: an hour
    [InlineData(15 * 24, 60, 1440)]      // beyond 14 days: a day
    [InlineData(30 * 24, 60, 1440)]      // last 30 days
    public void Each_period_gets_its_slot(int periodHours, int intervalMinutes, int expectedMinutes)
    {
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes),
                     QueryStoreTimeSlots.SlotLength(TimeSpan.FromHours(periodHours), intervalMinutes));
    }

    [Theory]
    [InlineData(1,      1440)]
    [InlineData(7 * 24, 1440)]
    [InlineData(24,     30)]
    public void A_slot_is_never_shorter_than_one_interval(int periodHours, int intervalMinutes)
    {
        var slot = QueryStoreTimeSlots.SlotLength(TimeSpan.FromHours(periodHours), intervalMinutes);
        Assert.True(slot >= TimeSpan.FromMinutes(intervalMinutes));
    }

    [Fact]
    public void A_missing_interval_length_counts_as_an_hour() =>
        Assert.Equal(TimeSpan.FromHours(1), QueryStoreTimeSlots.SlotLength(TimeSpan.FromHours(1), 0));

    [Fact]
    public void Day_slots_start_at_local_midnight_and_shorter_ones_at_a_utc_hour()
    {
        var india = TimeSpan.FromMinutes(330);

        Assert.Equal(india, QueryStoreTimeSlots.Origin(TimeSpan.FromDays(1), india).Offset);
        Assert.Equal(TimeSpan.Zero, QueryStoreTimeSlots.Origin(TimeSpan.FromHours(1), india).Offset);
    }

    [Fact]
    public void An_interval_falls_in_the_slot_its_start_is_in()
    {
        var origin = QueryStoreTimeSlots.Origin(TimeSpan.FromHours(1), TimeSpan.Zero);
        var slot   = TimeSpan.FromHours(1);

        // Two 30-minute intervals share the 14:00 slot.
        var a = QueryStoreTimeSlots.SlotIndex(origin, slot, new DateTime(2026, 9, 2, 14, 0, 0, DateTimeKind.Utc));
        var b = QueryStoreTimeSlots.SlotIndex(origin, slot, new DateTime(2026, 9, 2, 14, 30, 0, DateTimeKind.Utc));

        Assert.Equal(a, b);
        Assert.Equal(new DateTime(2026, 9, 2, 14, 0, 0, DateTimeKind.Utc), QueryStoreTimeSlots.SlotStartUtc(origin, slot, a));
    }

    [Fact]
    public void A_local_day_slot_starts_at_local_midnight()
    {
        var offset = TimeSpan.FromHours(2);
        var origin = QueryStoreTimeSlots.Origin(TimeSpan.FromDays(1), offset);

        // 23:30 UTC on 1 Sep is 01:30 on 2 Sep locally.
        var index = QueryStoreTimeSlots.SlotIndex(origin, TimeSpan.FromDays(1), new DateTime(2026, 9, 1, 23, 30, 0, DateTimeKind.Utc));

        Assert.Equal(new DateTime(2026, 9, 1, 22, 0, 0, DateTimeKind.Utc),
                     QueryStoreTimeSlots.SlotStartUtc(origin, TimeSpan.FromDays(1), index));
    }

    [Fact]
    public void The_slots_between_two_times_cover_the_period()
    {
        var origin = QueryStoreTimeSlots.Origin(TimeSpan.FromHours(1), TimeSpan.Zero);
        var from   = new DateTime(2026, 9, 2, 10, 20, 0, DateTimeKind.Utc);

        var slots = QueryStoreTimeSlots.SlotsBetween(origin, TimeSpan.FromHours(1), from, from.AddHours(24)).ToList();

        // 10:00 on the 2nd to 10:00 on the 3rd.
        Assert.Equal(25, slots.Count);
        Assert.Equal(slots.Count, slots.Distinct().Count());
    }
}
