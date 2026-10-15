using System.IO;
using Microsoft.Extensions.Time.Testing;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Alerts' view model on its own (#187): no page, no STA thread, a fake alert feed. It lists the
/// alerts saved and those active now, active first; filters by connection, status and period,
/// reading again after a change; follows the monitoring's alert changes only while the page is
/// shown; keeps the alert selected across a read; and says once what it couldn't read.
/// </summary>
public sealed class AlertsViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    private readonly FakeUiDispatcher _ui = new();
    private readonly FakeAlertFeed _feed = new();
    private readonly ConnectionSettings _hot = new() { Server = "hot", Name = "Hot", Database = "Sales", Authentication = SqlAuthMode.Windows };
    private readonly ConnectionSettings _quiet = new() { Server = "quiet", Name = "Quiet", Database = "Sales", Authentication = SqlAuthMode.Windows };

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    private Alert Cpu(ConnectionSettings conn, HealthLevel level, int minutesAgo, int? endedMinutesAgo = null) =>
        new(Guid.NewGuid(), conn.Id, HealthIndicator.Cpu, level, Now.AddMinutes(-minutesAgo),
            endedMinutesAgo is { } ended ? Now.AddMinutes(-ended) : null, 99, 90, "CPU at 99 %",
            endedMinutesAgo is null ? null : AlertEndReason.Recovered);

    private (AlertsViewModel Model, FakeShellStatus Shell, FakeNavigator Navigator) Connected(Guid? connectionId = null)
    {
        var settings = new ConnectionSettingsService(_dir);
        settings.Upsert(_hot);
        settings.Upsert(_quiet);
        var navigator = new FakeNavigator();
        var model     = new AlertsViewModel(_feed, settings, navigator, connectionId, _time);
        var shell     = new FakeShellStatus();
        model.Connect("Alerts", shell, new RefreshScheduler(_time, _ui));
        return (model, shell, navigator);
    }

    // A read the scheduler started runs its history read off the UI thread: waits for it.
    private static async Task Until(Func<bool> done)
    {
        for (var i = 0; i < 200 && !done(); i++)
            await Task.Delay(10);
        Assert.True(done());
    }

    [Fact]
    public async Task Loaded_ListsTheActiveAlertsFirst_ThenThePastOnes_WithTheirConnections()
    {
        var past     = Cpu(_quiet, HealthLevel.Warning, 120, endedMinutesAgo: 100);
        var critical = Cpu(_hot, HealthLevel.Critical, 5);
        _feed.Stored.Add(new(past, "Quiet"));
        _feed.Active.Add(critical);
        _feed.Monitored.Add(_hot.Id);
        var (model, shell, _) = Connected();

        await model.OnNavigatedToAsync();

        Assert.Equal(["Read Load"], shell.Reports);
        Assert.Equal([critical.Id, past.Id], model.Alerts.Select(a => a.Alert.Id));
        Assert.Equal(["Hot", "Quiet"], model.Alerts.Select(a => a.ConnectionName));
        Assert.Equal("2 alerts", model.CountText);
        Assert.False(model.ShowsNoData);
        Assert.StartsWith("An alert starts after ", model.Note);
        Assert.Equal(VerdictLevel.Critical, model.Verdict!.Level);
        Assert.StartsWith("1 active alert; ", model.Verdict.Headline);
        // The last 7 days, to a minute ahead.
        var (connection, from, to) = Assert.Single(_feed.Reads);
        Assert.Null(connection);
        Assert.Equal(Now.AddDays(-7), from);
        Assert.Equal(Now.AddMinutes(1), to);
    }

    [Fact]
    public async Task AFilter_ReadsAgain_OnlyOnceThePageHasRead()
    {
        _feed.Stored.Add(new(Cpu(_quiet, HealthLevel.Warning, 120, endedMinutesAgo: 100), "Quiet"));
        _feed.Active.Add(Cpu(_hot, HealthLevel.Critical, 5));
        var (model, _, _) = Connected();

        model.PeriodIndex = 0;   // before the first read: it uses it
        Assert.Empty(_feed.Reads);
        await model.OnNavigatedToAsync();
        Assert.Equal(Now.AddDays(-1), _feed.Reads[^1].FromUtc);

        model.StatusIndex = 2;   // past only
        await Until(() => _feed.Reads.Count == 2);
        await Until(() => !model.IsBusy);
        Assert.Equal(["Ended"], model.Alerts.Select(a => a.StatusText));

        model.SelectedConnection = model.Connections.Single(c => c.Name == "Hot");
        await Until(() => _feed.Reads.Count == 3);
        await Until(() => !model.IsBusy);
        Assert.Equal(_hot.Id, _feed.Reads[^1].ConnectionId);
        Assert.Empty(model.Alerts);
        Assert.Equal("No past alerts for this connection in the last 24 hours.", model.NoDataText);
        // Hot isn't monitored: no new alerts are raised for it, and the note says why.
        Assert.StartsWith("No new alerts are raised for this connection. Stopped by you.", model.Note);
    }

    [Fact]
    public async Task OpenedOnAConnection_FiltersOnIt_FromTheFirstRead()
    {
        var (model, _, _) = Connected(_quiet.Id);

        await model.OnNavigatedToAsync();

        Assert.Equal("Quiet", model.SelectedConnection.Name);
        Assert.Equal(_quiet.Id, Assert.Single(_feed.Reads).ConnectionId);
        Assert.Equal(["All connections", "Hot", "Quiet"], model.Connections.Select(c => c.Name));
    }

    [Fact]
    public async Task NoConnectionChosen_IsAllConnections()
    {
        var (model, shell, _) = Connected(_quiet.Id);
        await model.OnNavigatedToAsync();

        model.SelectedConnection = null!;   // as the combo box can push
        await Until(() => _feed.Reads.Count == 2);
        await Until(() => !model.IsBusy);

        Assert.Equal("All connections", model.SelectedConnection.Name);
        Assert.Null(_feed.Reads[^1].ConnectionId);
        Assert.DoesNotContain(shell.Reports, r => r.StartsWith("Error", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Empty_SaysWhatIsChecked()
    {
        var (model, _, _) = Connected();

        await model.OnNavigatedToAsync();

        Assert.Empty(model.Alerts);
        Assert.True(model.ShowsNoData);
        Assert.StartsWith("No alerts in the last 7 days. Every sample of a monitored connection is checked", model.NoDataText);
        Assert.Equal("Alerts", model.CountText);
    }

    [Fact]
    public async Task AnError_IsReportedOnce_AndTheVerdictSaysWhatCouldntBeRead()
    {
        _feed.ReadFails = new InvalidOperationException("database is locked");
        var (model, shell, _) = Connected();

        await model.OnNavigatedToAsync();

        Assert.Equal(["Error Load: database is locked"], shell.Reports);
        Assert.Equal(PageVerdict.Unreadable("the alert history", "Error: database is locked"), model.Verdict);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task AHistoryFromANewerSqlVitals_ListsTheActiveAlertsOnly_AndSaysSo()
    {
        _feed.ReadFails = new HistorySchemaTooNewException("history.db", 99);
        _feed.Active.Add(Cpu(_hot, HealthLevel.Warning, 5));
        var (model, shell, _) = Connected();

        await model.OnNavigatedToAsync();

        Assert.Equal(["Read Load"], shell.Reports);
        Assert.Single(model.Alerts);
        Assert.StartsWith("The history file is from a newer SqlVitals, so only the alerts active now are listed.", model.Note);
    }

    [Fact]
    public async Task ARead_KeepsTheAlertSelected()
    {
        var active = Cpu(_hot, HealthLevel.Warning, 5);
        _feed.Active.Add(active);
        _feed.Stored.Add(new(Cpu(_quiet, HealthLevel.Warning, 120, endedMinutesAgo: 100), "Quiet"));
        var (model, _, _) = Connected();
        await model.OnNavigatedToAsync();
        model.SelectedAlert = model.Alerts[1];
        var picked = model.SelectedAlert.Alert.Id;

        _feed.Active.Clear();
        _feed.Active.Add(active with { Severity = HealthLevel.Critical });
        await model.RefreshAsync();

        Assert.Equal(picked, model.SelectedAlert!.Alert.Id);
        Assert.Equal("Critical", model.Alerts[0].SeverityText);
    }

    [Fact]
    public async Task AlertChanges_AreFollowedWhileShown_AndReadOnceShortlyAfter()
    {
        var (model, _, _) = Connected();
        Assert.Equal(0, _feed.Followers);

        await model.OnNavigatedToAsync();
        Assert.Equal(2, _feed.Followers);

        // A burst of changes reads once, a moment after the last.
        _feed.RaiseAlertsChanged(_hot.Id);
        _feed.RaiseMonitoringChanged();
        _time.Advance(AlertsViewModel.ReloadDelay);
        _ui.RunPosted();
        await Until(() => _feed.Reads.Count == 2);
        await Until(() => !model.IsBusy);
        _time.Advance(AlertsViewModel.ReloadDelay);
        _ui.RunPosted();
        Assert.Equal(2, _feed.Reads.Count);

        model.OnNavigatedFrom();
        Assert.Equal(0, _feed.Followers);
    }

    [Fact]
    public async Task WhileShown_ReadsEvery30s_SoTheDurationsMove()
    {
        var (model, _, _) = Connected();
        await model.OnNavigatedToAsync();

        _time.Advance(AlertsViewModel.DurationsInterval);
        _ui.RunPosted();
        await Until(() => _feed.Reads.Count == 2);

        model.OnNavigatedFrom();
        _time.Advance(AlertsViewModel.DurationsInterval);
        _ui.RunPosted();
        Assert.Equal(2, _feed.Reads.Count);
    }

    [Fact]
    public async Task Investigate_OpensAnAlertsEvidence_FromItsRow()
    {
        var alert = Cpu(_hot, HealthLevel.Critical, 5);
        _feed.Active.Add(alert);
        var (model, _, navigator) = Connected();
        await model.OnNavigatedToAsync();

        await model.InvestigateCommand.ExecuteAsync(model.Alerts[0]);
        await model.InvestigateCommand.ExecuteAsync(null);

        Assert.Equal((alert, ActivitySources.AlertsPage), Assert.Single(navigator.Investigated));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
