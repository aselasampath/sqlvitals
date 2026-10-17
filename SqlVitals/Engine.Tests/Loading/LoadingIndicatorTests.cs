using SqlVitals.Engine.Loading;

namespace SqlVitals.Engine.Tests.Loading;

/// <summary>
/// When the loading bar shows and what it says (#117): not for a fast read, at least its shortest
/// time once shown, the time waited from 3 s and when it gives up from 10 s, and of the waits
/// running, only the latest one, whatever the others do.
/// </summary>
public sealed class LoadingIndicatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 17, 9, 0, 0, TimeSpan.Zero);

    private static readonly Wait Backups = new("Loading Backups", "Backups loaded",
        new WaitLimit(TimeSpan.FromSeconds(30), WaitLimitKind.EachQuery, "Settings → Command timeout"), CanStop: true);

    private static DateTimeOffset At(double seconds) => T0 + TimeSpan.FromSeconds(seconds);

    // ── When it shows ────────────────────────────────────────────────────

    [Fact]
    public void AFastRead_NeverShows()
    {
        var bar = new LoadingIndicator();
        var id  = bar.Begin(Backups, T0);
        Assert.Null(bar.Show(At(0.2)));

        Assert.True(bar.End(id));
        Assert.Null(bar.Show(At(0.2)));
        Assert.Null(bar.Show(At(0.4)));
        Assert.Null(bar.NextChange(At(0.2)));
    }

    [Fact]
    public void ASlowerRead_ShowsAfter300ms_WithStop()
    {
        var bar = new LoadingIndicator();
        var id  = bar.Begin(Backups, T0);
        Assert.Equal(At(0.3), bar.NextChange(T0));

        var view = bar.Show(At(0.4))!;
        Assert.Equal(id, view.Id);
        Assert.Equal("Loading Backups…", view.Text);
        Assert.True(view.CanStop);
        Assert.False(view.Lingering);
    }

    [Fact]
    public void OnceShown_ItStaysAtLeast300ms_WithoutStop_ThenGoes()
    {
        var bar = new LoadingIndicator();
        var id  = bar.Begin(Backups, T0);
        Assert.NotNull(bar.Show(At(0.3)));

        bar.End(id);
        var lingering = bar.Show(At(0.4))!;
        Assert.True(lingering.Lingering);
        Assert.False(lingering.CanStop);
        Assert.Equal("Loading Backups…", lingering.Text);
        Assert.Equal(At(0.6), bar.NextChange(At(0.4)));

        Assert.Null(bar.Show(At(0.6)));
        Assert.Null(bar.NextChange(At(0.6)));
    }

    [Fact]
    public void ShownLongEnough_ItGoesAsSoonAsTheReadEnds()
    {
        var bar = new LoadingIndicator();
        var id  = bar.Begin(Backups, T0);
        Assert.NotNull(bar.Show(At(0.3)));
        Assert.NotNull(bar.Show(At(2)));

        bar.End(id);
        Assert.Null(bar.Show(At(2)));
    }

    [Fact]
    public void Cleared_ItGoesAtOnce_AndAnEndHeardAfterwardsIsIgnored()
    {
        var bar = new LoadingIndicator();
        var id  = bar.Begin(Backups, T0);
        Assert.NotNull(bar.Show(At(1)));

        bar.Clear();
        Assert.Null(bar.Show(At(1)));
        Assert.False(bar.End(id));
        Assert.Null(bar.Running(id));
    }

    // ── What it says ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(0.0,  "Loading Backups…")]
    [InlineData(2.9,  "Loading Backups…")]
    [InlineData(3.0,  "Loading Backups… 3 s")]
    [InlineData(7.6,  "Loading Backups… 7 s")]
    [InlineData(9.9,  "Loading Backups… 9 s")]
    [InlineData(10.0, "Loading Backups… 10 s · each query gives up after 30 s (Settings → Command timeout)")]
    [InlineData(45.0, "Loading Backups… 45 s · each query gives up after 30 s (Settings → Command timeout)")]
    public void TheText_AddsTheTimeWaitedFrom3s_AndWhenItGivesUpFrom10s(double seconds, string text) =>
        Assert.Equal(text, LoadingIndicator.Text(Backups, TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void AWaitWithoutALimit_OnlyCountsTheSeconds() =>
        Assert.Equal("Loading Overview… 12 s", LoadingIndicator.Text(new Wait("Loading Overview"), TimeSpan.FromSeconds(12)));

    [Fact]
    public void ANote_IsSaidFromTheStart_AfterTheRest()
    {
        var connecting = new Wait("Connecting to Sales-Prod", Limit: new WaitLimit(TimeSpan.FromSeconds(15), WaitLimitKind.Whole),
                                  Note: "complete the Microsoft sign-in if prompted");

        Assert.Equal("Connecting to Sales-Prod… complete the Microsoft sign-in if prompted",
                     LoadingIndicator.Text(connecting, TimeSpan.Zero));
        Assert.Equal("Connecting to Sales-Prod… 12 s · gives up after 15 s · complete the Microsoft sign-in if prompted",
                     LoadingIndicator.Text(connecting, TimeSpan.FromSeconds(12)));
    }

    [Theory]
    [InlineData(30,  WaitLimitKind.EachQuery,       "each query gives up after 30 s")]
    [InlineData(120, WaitLimitKind.EachQueryAtMost, "each query gives up after at most 2 min")]
    [InlineData(90,  WaitLimitKind.EachQueryAtMost, "each query gives up after at most 90 s")]
    [InlineData(15,  WaitLimitKind.Whole,           "gives up after 15 s")]
    [InlineData(600, WaitLimitKind.Whole,           "gives up after 10 min")]
    public void TheLimit_InWholeMinutesOrSeconds(int seconds, WaitLimitKind kind, string text) =>
        Assert.Equal(text, LoadingIndicator.LimitText(new WaitLimit(TimeSpan.FromSeconds(seconds), kind)));

    [Theory]
    [InlineData(0.4, "1 s")]
    [InlineData(14.8, "14 s")]
    [InlineData(120, "2 min")]
    public void TheTimeWaited_IsWholeSeconds_AtLeastOne(double seconds, string text) =>
        Assert.Equal(text, LoadingIndicator.Waited(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void ItAsksAgain_EachSecond_OnceTheSecondsShow()
    {
        var bar = new LoadingIndicator();
        bar.Begin(Backups, T0);
        Assert.NotNull(bar.Show(At(0.3)));

        // Nothing changes between 300 ms and 3 s, then every whole second.
        Assert.Equal(At(3), bar.NextChange(At(0.3)));
        Assert.Equal(At(4), bar.NextChange(At(3)));
        Assert.Equal(At(13), bar.NextChange(At(12.4)));
    }

    // ── Waits that overlap ───────────────────────────────────────────────

    [Fact]
    public void TheLatestWaitRunning_IsShown_AndAnOlderOneEndingDoesntHideIt()
    {
        var bar   = new LoadingIndicator();
        var older = bar.Begin(Backups, T0);
        var part  = bar.Begin(new Wait("Checking fragmentation"), At(1));
        Assert.Equal(older, bar.Show(At(1.1))!.Id);
        Assert.Equal(part, bar.Show(At(1.3))!.Id);

        bar.End(older);
        var view = bar.Show(At(1.4))!;
        Assert.Equal(part, view.Id);
        Assert.Equal("Checking fragmentation…", view.Text);
        Assert.False(view.CanStop);
    }

    [Fact]
    public void TheLatestEnding_HandsTheBarBackToTheOneStillRunning()
    {
        var bar   = new LoadingIndicator();
        var older = bar.Begin(Backups, T0);
        var part  = bar.Begin(new Wait("Checking fragmentation"), At(1));
        Assert.Equal(part, bar.Show(At(2))!.Id);

        bar.End(part);
        var view = bar.Show(At(4))!;
        Assert.Equal(older, view.Id);
        Assert.Equal("Loading Backups… 4 s", view.Text);
        Assert.False(view.Lingering);
    }

    [Fact]
    public void AnEndHeardTwice_ChangesNothing()
    {
        var bar  = new LoadingIndicator();
        var id   = bar.Begin(Backups, T0);
        var next = bar.Begin(Backups with { Doing = "Refreshing Backups" }, At(1));

        Assert.True(bar.End(id));
        Assert.False(bar.End(id));
        Assert.Equal(next, bar.Show(At(2))!.Id);
    }
}
