using System.Globalization;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Tests.Alerts;

public class AlertNotificationTests
{
    private static readonly Guid Connection = Guid.NewGuid();
    private static readonly DateTime Start  = new(2026, 9, 25, 9, 0, 0, DateTimeKind.Utc);

    private static T Invariant<T>(Func<T> action)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try { return action(); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static Alert AlertOf(HealthIndicator indicator, HealthLevel severity, double value = 95,
                                 double? threshold = 90, string detail = "CPU 95%") =>
        new(Guid.NewGuid(), Connection, indicator, severity, Start, null, value, threshold, detail);

    private static AlertChange Change(Alert alert, AlertChangeKind kind) => new(alert, kind);

    private static AlertNotification? From(params AlertChange[] changes) =>
        Invariant(() => AlertNotification.From(Connection, "Sales-Prod", changes));

    [Fact]
    public void AStartedAlert_NamesTheIndicatorConnectionAndThreshold()
    {
        var n = From(Change(AlertOf(HealthIndicator.Cpu, HealthLevel.Critical), AlertChangeKind.Started));

        Assert.NotNull(n);
        Assert.Equal(Connection, n.ConnectionId);
        Assert.Equal(HealthLevel.Critical, n.Severity);
        Assert.Equal("Critical: CPU on Sales-Prod", n.Title);
        Assert.Equal("CPU 95% (threshold ≥ 90 %).\n" + AlertNotification.ClickHint, n.Text);
    }

    [Fact]
    public void AnEscalation_SaysItIsNowCritical()
    {
        var n = From(Change(AlertOf(HealthIndicator.Blocking, HealthLevel.Critical, 400, 300, "Blocking 6.7 min (4 blocked)"),
                            AlertChangeKind.Escalated));

        Assert.Equal("Now critical: Blocking on Sales-Prod", n!.Title);
        Assert.StartsWith("Blocking 6.7 min (4 blocked) (threshold ≥ 5 min).", n.Text);
    }

    [Theory]
    [InlineData(AlertChangeKind.Worsened)]
    [InlineData(AlertChangeKind.Ended)]
    public void WorseningOrEnding_DoesNotNotify(AlertChangeKind kind) =>
        Assert.Null(From(Change(AlertOf(HealthIndicator.Cpu, HealthLevel.Warning), kind)));

    [Fact]
    public void NoChanges_DoesNotNotify() => Assert.Null(From());

    [Fact]
    public void AlertsStartingTogether_AreOneNotification_CriticalFirst()
    {
        var n = From(
            Change(AlertOf(HealthIndicator.LogSpace, HealthLevel.Warning, 82, 80, "Log 82% full (Sales)"), AlertChangeKind.Started),
            Change(AlertOf(HealthIndicator.Cpu, HealthLevel.Critical), AlertChangeKind.Started),
            Change(AlertOf(HealthIndicator.TempDbSpace, HealthLevel.Warning, 70, 60, "TempDB 70% full"), AlertChangeKind.Worsened));

        Assert.Equal(HealthLevel.Critical, n!.Severity);
        Assert.Equal("2 alerts on Sales-Prod (1 critical)", n.Title);
        Assert.Equal("Critical: CPU 95%\nWarning: Log 82% full (Sales)\n" + AlertNotification.ClickHint, n.Text);
    }

    [Fact]
    public void WithoutDetail_TheFigureIsShown()
    {
        var n = From(Change(AlertOf(HealthIndicator.PageLifeExpectancy, HealthLevel.Warning, 120, 300, detail: ""),
                            AlertChangeKind.Started));

        Assert.StartsWith("Page life expectancy 120 s (threshold < 300 s).", n!.Text);
    }

    [Fact]
    public void AnUnknownThreshold_IsLeftOut()
    {
        var n = From(Change(AlertOf(HealthIndicator.Cpu, HealthLevel.Warning, threshold: null), AlertChangeKind.Started));

        Assert.Equal("CPU 95%.\n" + AlertNotification.ClickHint, n!.Text);
    }

    [Fact]
    public void LongTexts_FitWhatWindowsTakes_AndKeepTheHint()
    {
        var name    = new string('S', 100);
        var changes = Enum.GetValues<HealthIndicator>()
            .Select(i => Change(AlertOf(i, HealthLevel.Warning, detail: new string('x', 70)), AlertChangeKind.Started))
            .ToArray();

        var n = Invariant(() => AlertNotification.From(Connection, name, changes))!;

        Assert.True(n.Title.Length <= AlertNotification.MaxTitleLength);
        Assert.EndsWith("…", n.Title);
        Assert.True(n.Text.Length <= AlertNotification.MaxTextLength);
        Assert.Contains("more", n.Text);
        Assert.EndsWith(AlertNotification.ClickHint, n.Text);
    }

    [Fact]
    public void TrayToolTip_CountsActiveAlerts()
    {
        var warning  = AlertOf(HealthIndicator.Cpu, HealthLevel.Warning);
        var critical = AlertOf(HealthIndicator.Blocking, HealthLevel.Critical);

        Assert.Equal("SqlVitals · no active alerts", AlertNotification.TrayToolTip([]));
        Assert.Equal("SqlVitals · 1 active alert (warning)", AlertNotification.TrayToolTip([warning]));
        Assert.Equal("SqlVitals · 1 active alert (critical)", AlertNotification.TrayToolTip([critical]));
        Assert.Equal("SqlVitals · 2 active alerts", AlertNotification.TrayToolTip([warning, warning]));
        Assert.Equal("SqlVitals · 2 active alerts (1 critical)", AlertNotification.TrayToolTip([warning, critical]));
    }
}
