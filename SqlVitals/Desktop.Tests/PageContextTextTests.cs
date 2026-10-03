using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Tests;

/// <summary>The top bar's words for a page's freshness (#140), on a fixed clock.</summary>
public sealed class PageContextTextTests
{
    private static readonly DateTime Now = new(2026, 10, 3, 14, 30, 0);

    [Fact]
    public void Live_SaysHowOftenAndAsOfWhen()
    {
        var live = new PageFreshness(PageKind.Live, TimeSpan.FromSeconds(5), AsOf: new DateTime(2026, 10, 3, 14, 29, 56));

        Assert.Equal("Live", PageContextText.Badge(live));
        Assert.Equal("every 5 s · As of 14:29:56", PageContextText.Detail(live, Now));
        Assert.Equal("Live, updates every 5 seconds, as of 14:29:56", PageContextText.AutomationName(live, Now));
    }

    [Fact]
    public void Live_Paused_SaysSo_AndKeepsTheTimeOfTheLastUpdate()
    {
        var paused = new PageFreshness(PageKind.Live, TimeSpan.FromMinutes(1), Paused: true, AsOf: Now.AddMinutes(-3));

        Assert.Equal("Paused", PageContextText.Badge(paused));
        Assert.Equal("every 1 min · As of 14:27:00", PageContextText.Detail(paused, Now));
        Assert.StartsWith("Live, paused, updates every 1 minute", PageContextText.AutomationName(paused, Now));
    }

    [Fact]
    public void Live_BeforeItsFirstUpdate_SaysOnlyTheInterval()
    {
        Assert.Equal("every 10 s", PageContextText.Detail(new PageFreshness(PageKind.Live, TimeSpan.FromSeconds(10)), Now));
        Assert.Equal(string.Empty, PageContextText.Detail(new PageFreshness(PageKind.Live), Now));
    }

    [Theory]
    [InlineData(0,    "just now")]
    [InlineData(59,   "just now")]
    [InlineData(60,   "1 min ago")]
    [InlineData(150,  "2 min ago")]
    [InlineData(3600, "1 h ago")]
    [InlineData(3900, "1 h 5 min ago")]
    [InlineData(90000, "1 d ago")]
    public void Snapshot_SaysWhenItWasTaken_AndHowLongAgo(int secondsAgo, string age)
    {
        var taken = Now.AddSeconds(-secondsAgo);
        var snapshot = new PageFreshness(PageKind.Snapshot, Taken: taken);

        Assert.Equal("Snapshot", PageContextText.Badge(snapshot));
        Assert.Equal($"Taken {taken:HH:mm:ss} · {age}", PageContextText.Detail(snapshot, Now));
    }

    [Fact]
    public void Snapshot_TurnsStale_AtFifteenMinutes()
    {
        var snapshot = new PageFreshness(PageKind.Snapshot, Taken: Now);

        Assert.False(PageContextText.IsStale(snapshot, Now.AddMinutes(14).AddSeconds(59)));
        Assert.True(PageContextText.IsStale(snapshot, Now.AddMinutes(15)));
        Assert.Equal("Snapshot, taken at 14:30:00, 15 minutes ago, more than 15 minutes old",
                     PageContextText.AutomationName(snapshot, Now.AddMinutes(15)));

        // Only a snapshot goes stale: a live page's time is when it last moved, and history's is its window.
        Assert.False(PageContextText.IsStale(new PageFreshness(PageKind.Live, AsOf: Now), Now.AddHours(1)));
        Assert.False(PageContextText.IsStale(new PageFreshness(PageKind.Snapshot), Now.AddHours(1)));
    }

    [Fact]
    public void History_SaysItsWindow_WhatItIsComparedWith_AndWhereTheDataEnds()
    {
        var history = new PageFreshness(PageKind.History, Window: "Last 24 hours", CompareWith: "the 7 days before",
                                         DataTo: new DateTime(2026, 10, 3, 11, 55, 0));

        Assert.Equal("History", PageContextText.Badge(history));
        Assert.Equal("Last 24 hours · compared with the 7 days before · Data to 11:55", PageContextText.Detail(history, Now));
        Assert.Equal("History, Last 24 hours, compared with the 7 days before, data to 11:55",
                     PageContextText.AutomationName(history, Now));

        // Data from another day says which.
        var older = history with { Window = null, CompareWith = null, DataTo = new DateTime(2026, 10, 1, 9, 5, 0) };
        Assert.Equal($"Data to {new DateTime(2026, 10, 1, 9, 5, 0):d MMM HH:mm}", PageContextText.Detail(older, Now));
    }

    [Fact]
    public void APageWithNoKind_HasNoBadge()
    {
        var none = new PageFreshness(PageKind.None);
        Assert.Equal(string.Empty, PageContextText.Badge(none));
        Assert.Equal(string.Empty, PageContextText.Detail(none, Now));
        Assert.Equal(string.Empty, PageContextText.AutomationName(none, Now));
    }

    [Theory]
    [InlineData(5,    "5 s")]
    [InlineData(60,   "1 min")]
    [InlineData(90,   "1 min 30 s")]
    [InlineData(7200, "2 h")]
    public void Interval_IsShort(int seconds, string text) =>
        Assert.Equal(text, PageContextText.Interval(TimeSpan.FromSeconds(seconds)));
}
