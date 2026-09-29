using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Tests.Monitoring;

public sealed class MonitoringPlanTests
{
    private static readonly Guid Active     = Guid.Parse("00000000-0000-4000-8000-00000000000a");
    private static readonly Guid Background = Guid.Parse("00000000-0000-4000-8000-00000000000b");
    private static readonly Guid Other      = Guid.Parse("00000000-0000-4000-8000-00000000000c");

    private static readonly DateTime At = new(2026, 9, 29, 10, 42, 0);

    // The active connection, one with Monitor in background, and one without.
    private static MonitoringCandidate[] Connections(bool needsPassword = false, bool needsSignIn = false) =>
    [
        new(Active,     IsActive: true,  MonitorInBackground: false, NeedsPassword: false, NeedsSignIn: false),
        new(Background, IsActive: false, MonitorInBackground: true,  NeedsPassword: needsPassword, NeedsSignIn: needsSignIn),
        new(Other,      IsActive: false, MonitorInBackground: false, NeedsPassword: false, NeedsSignIn: false),
    ];

    private static Guid[] Monitored(bool automatic, MonitoringOverrides overrides, MonitoringCandidate[]? connections = null) =>
        MonitoringPlan.Decide(automatic, connections ?? Connections(), overrides)
                      .Where(d => d.Value.Monitor).Select(d => d.Key).Order().ToArray();

    [Fact]
    public void Off_MonitorsNone()
    {
        var decisions = MonitoringPlan.Decide(false, Connections(), new MonitoringOverrides());

        Assert.All(decisions.Values, d => Assert.Equal(MonitoringReason.AutomaticOff, d.Reason));
        Assert.All(decisions.Values, d => Assert.True(d.CanStart));
        Assert.Equal(MonitoringPlan.AutomaticOffText, decisions[Active].Text);
    }

    [Fact]
    public void On_MonitorsTheActiveConnectionAndTheBackgroundOnes()
    {
        var decisions = MonitoringPlan.Decide(true, Connections(), new MonitoringOverrides());

        Assert.Equal(new[] { Active, Background }, Monitored(true, new MonitoringOverrides()));
        Assert.Equal(MonitoringReason.Automatic, decisions[Active].Reason);
        Assert.Equal(MonitoringReason.BackgroundOff, decisions[Other].Reason);
    }

    [Fact]
    public void Off_ThenStartOnABackgroundConnection_MonitorsOnlyThatOne()
    {
        var overrides = new MonitoringOverrides();
        overrides.Start(Background);

        Assert.Equal(new[] { Background }, Monitored(false, overrides));
        Assert.Equal(MonitoringReason.StartedByUser,
                     MonitoringPlan.Decide(false, Connections(), overrides)[Background].Reason);
    }

    [Fact]
    public void Off_ThenStartOnAConnectionWithoutMonitorInBackground_MonitorsIt()
    {
        var overrides = new MonitoringOverrides();
        overrides.Start(Other);

        Assert.Equal(new[] { Other }, Monitored(false, overrides));
        Assert.Equal(new[] { Active, Background, Other }, Monitored(true, overrides));
    }

    [Fact]
    public void On_ThenStopOnTheActiveOne_MonitorsAllButTheActiveOne()
    {
        var overrides = new MonitoringOverrides();
        overrides.Stop(Active, At);

        var decision = MonitoringPlan.Decide(true, Connections(), overrides)[Active];

        Assert.Equal(new[] { Background }, Monitored(true, overrides));
        Assert.Equal(MonitoringReason.StoppedByUser, decision.Reason);
        Assert.Contains("Stopped by you at 10:42", decision.Text);
        Assert.True(decision.CanStart);
    }

    [Fact]
    public void TheLatestPressWins()
    {
        var overrides = new MonitoringOverrides();
        overrides.Stop(Background, At);
        overrides.Start(Background);
        Assert.Equal(new[] { Background }, Monitored(false, overrides));

        overrides.Stop(Background, At);
        Assert.Empty(Monitored(false, overrides));
    }

    [Fact]
    public void AStartedConnectionKeepsRunningWhenAnotherBecomesActive()
    {
        var overrides = new MonitoringOverrides();
        overrides.Start(Active);

        // The user switches to Other: Active is no longer active, but was started by hand.
        MonitoringCandidate[] switched =
        [
            new(Active, false, false, false, false),
            new(Background, false, true, false, false),
            new(Other, true, false, false, false),
        ];

        Assert.Equal(new[] { Active }, Monitored(false, overrides, switched));
    }

    [Fact]
    public void Off_SwitchingToAnotherConnectionDoesNotMonitorIt()
    {
        MonitoringCandidate[] switched =
        [
            new(Active, false, false, false, false),
            new(Other, true, false, false, false),
        ];

        Assert.Empty(Monitored(false, new MonitoringOverrides(), switched));
    }

    [Fact]
    public void AMissingPassword_CantBeMonitoredEvenWhenStarted()
    {
        var overrides = new MonitoringOverrides();
        overrides.Start(Background);

        var decision = MonitoringPlan.Decide(true, Connections(needsPassword: true), overrides)[Background];

        Assert.False(decision.Monitor);
        Assert.False(decision.CanStart);
        Assert.Equal(MonitoringReason.NeedsPassword, decision.Reason);
        Assert.Contains("password isn't saved", decision.Text);
    }

    [Fact]
    public void AnEntraConnectionNotSignedIn_CantBeMonitoredYet()
    {
        var decision = MonitoringPlan.Decide(true, Connections(needsSignIn: true), new MonitoringOverrides())[Background];

        Assert.False(decision.Monitor);
        Assert.False(decision.CanStart);
        Assert.Equal(MonitoringReason.NeedsSignIn, decision.Reason);
    }

    [Fact]
    public void Clear_ForgetsEveryPress()
    {
        var overrides = new MonitoringOverrides();
        overrides.Start(Other);
        overrides.Stop(Active, At);

        overrides.Clear();

        Assert.Equal(new[] { Active, Background }, Monitored(true, overrides));
        Assert.Empty(Monitored(false, overrides));
    }

    [Fact]
    public void KeepOnly_ForgetsDeletedConnections()
    {
        var overrides = new MonitoringOverrides();
        overrides.Start(Other);
        overrides.Stop(Background, At);

        overrides.KeepOnly(new HashSet<Guid> { Active });

        Assert.False(overrides.IsStarted(Other));
        Assert.Null(overrides.StoppedAt(Background));
    }

    [Theory]
    [InlineData(null,  false, false)]   // a new installation: off
    [InlineData(null,  true,  true)]    // an upgrade with connections: on, as before
    [InlineData(false, true,  false)]   // saved off
    [InlineData(true,  false, true)]    // saved on
    public void StartAutomatically_FollowsTheUpgradeRule(bool? saved, bool hasConnections, bool expected) =>
        Assert.Equal(expected, MonitoringPlan.StartAutomatically(saved, hasConnections));
}
