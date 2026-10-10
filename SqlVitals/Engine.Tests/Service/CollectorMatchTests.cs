using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Service;

namespace SqlVitals.Engine.Tests.Service;

/// <summary>
/// Which collector servers are the app's connections, what a tile says of a server a collector
/// watches, and the plan leaving it to the collector (#172).
/// </summary>
public sealed class CollectorMatchTests
{
    private static readonly DateTime Now = new(2026, 10, 10, 9, 0, 0, DateTimeKind.Utc);

    private static StoredConnection Stored(Guid id, string server, string database, int minutesAgo = 0) =>
        new(id, server, server, database, Now.AddMinutes(-minutesAgo));

    [Fact]
    public void AHandedOverServer_IsItsConnection_ByIdWhateverItsName()
    {
        var id = Guid.NewGuid();

        var map = CollectorMatch.Match([new AppConnection(id, "sql01", "Sales")], [Stored(id, "renamed", "Other")]);

        Assert.Equal(id, map[id]);
    }

    [Fact]
    public void AServerAddedOnTheCollector_IsMatchedByServerAndDatabase_TheNewestOnce()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var old    = Stored(Guid.NewGuid(), "SQL01", "(default database)", minutesAgo: 60);
        var newest = Stored(Guid.NewGuid(), "sql01 ", "(DEFAULT DATABASE)", minutesAgo: 1);

        var map = CollectorMatch.Match(
            [new AppConnection(a, "sql01", "(default database)"), new AppConnection(b, "sql01", "(default database)")],
            [old, newest, Stored(Guid.NewGuid(), "sql02", "(default database)")]);

        Assert.Equal(newest.Id, map[a]);
        Assert.Equal(old.Id, map[b]);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void AnotherConnectionsId_IsntTakenByAName()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        // b was handed over; a has the same server, but b's history is b's.
        var map = CollectorMatch.Match([new AppConnection(a, "sql01", "Sales"), new AppConnection(b, "sql01", "Sales")], [Stored(b, "sql01", "Sales")]);

        Assert.Equal(b, map[b]);
        Assert.False(map.ContainsKey(a));
    }

    private static TileInput Input(ElsewhereWatch watch) =>
        new(Guid.NewGuid(), "Sales-Prod", "sql01", "Sales", IsActive: false, IsMonitored: false, Score: null, Elsewhere: watch);

    private static Alert Alerting(HealthIndicator indicator, HealthLevel severity) =>
        new(Guid.NewGuid(), Guid.NewGuid(), indicator, severity, Now.AddMinutes(-5), null, 95, 90, "x");

    [Theory]
    [InlineData(null,      true,  TileStatus.Healthy,     "No check has alerted.")]
    [InlineData("Warning", true,  TileStatus.Warning,     "Alerting on CPU.")]
    [InlineData("Critical", true, TileStatus.Critical,    "Alerting on CPU.")]
    [InlineData(null,      false, TileStatus.Unreachable, "Can't be reached or read, at the collector service's last sample")]
    public void AServerAServiceWatches_IsGradedByItsOpenAlerts(string? alert, bool reachable, TileStatus status, string reason)
    {
        var alerts = alert is null ? [] : new[] { Alerting(HealthIndicator.Cpu, Enum.Parse<HealthLevel>(alert)) };

        var tile = ServerTile.From(Input(new ElsewhereWatch("the collector service", new ScoreHistoryPoint(Now, 72, reachable), alerts)), Now.ToLocalTime());

        Assert.Equal(status, tile.Status);
        Assert.StartsWith(reason, tile.Reason);
        Assert.Equal("72", tile.ScoreText);
        Assert.StartsWith("Sample ", tile.Footer);
        Assert.EndsWith(" · by the collector service", tile.Footer);
        Assert.Equal(TileAction.Open, tile.Action);
        Assert.Equal(alert is null ? HealthLevel.Unknown : Enum.Parse<HealthLevel>(alert),
                     tile.Bars.Single(b => b.Indicator == HealthIndicator.Cpu).Level);
    }

    [Fact]
    public void AServerHandedToAStoppedService_IsOff_AndStartIsOffered()
    {
        var tile = ServerTile.From(Input(new ElsewhereWatch("the collector service", null, [],
                                                            "Handed to the collector service, which is stopped.")), Now);

        Assert.Equal(TileStatus.Off, tile.Status);
        Assert.Equal("Handed to the collector service, which is stopped.", tile.Reason);
        Assert.Equal(TileAction.Start, tile.Action);
        Assert.True(tile.CanAct);
    }

    [Fact]
    public void ThePlan_LeavesAServerTheServiceWatches_ToIt_WhateverElseItSays()
    {
        var overrides = new MonitoringOverrides();
        var id = Guid.NewGuid();
        overrides.Start(id);

        var decision = MonitoringPlan.Decide(true, new MonitoringCandidate(id, true, true, NeedsPassword: true, NeedsSignIn: false,
                                                                           WatchedElsewhere: "Watched by the collector service."), overrides);

        Assert.Equal(MonitoringReason.WatchedElsewhere, decision.Reason);
        Assert.False(decision.Monitor);
        Assert.True(decision.CanStart);
        Assert.Equal("Watched by the collector service.", decision.Text);
        Assert.Equal(WatchStop.WatchedElsewhere, WatchChange.StopFor(decision.Reason));
    }
}
