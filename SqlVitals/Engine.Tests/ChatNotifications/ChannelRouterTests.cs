using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Monitoring;
using static SqlVitals.Engine.Tests.ChatNotifications.AlertMessageTests;

namespace SqlVitals.Engine.Tests.ChatNotifications;

/// <summary>What a channel is told about a connection's alert changes: starts (#121) and ends (#123).</summary>
public sealed class ChannelRouterTests
{
    private static readonly Guid Other = Guid.Parse("00000000-0000-4000-8000-0000000000c2");

    private readonly ChannelRouter _router = new();

    private static ChatChannel Channel(bool criticalOnly = false, params Guid[] only) => new()
    {
        Name           = "DBA team",
        AllConnections = only.Length == 0,
        ConnectionIds  = only.ToList(),
        CriticalOnly   = criticalOnly,
    };

    private IReadOnlyList<ChatMessage> Route(ChatChannel channel, params AlertChange[] changes) =>
        _router.Route(channel, Connection, "Sales-Prod", changes, "DBA-PC01");

    private static AlertChange Started(Alert alert)  => new(alert, AlertChangeKind.Started);
    private static AlertChange Escalated(Alert alert) =>
        new(alert with { Severity = HealthLevel.Critical, Threshold = 90 }, AlertChangeKind.Escalated);
    private static AlertChange Worsened(Alert alert) => new(alert with { Value = 99 }, AlertChangeKind.Worsened);
    private static AlertChange Ended(Alert alert)    =>
        new(alert with { EndedUtc = alert.StartedUtc.AddMinutes(5), EndReason = AlertEndReason.Recovered }, AlertChangeKind.Ended);

    [Fact]
    public void AStart_IsPosted_AndRemembered()
    {
        var message = Assert.Single(Route(Channel(), Started(Cpu())));

        Assert.Equal("🔴 Critical: CPU on Sales-Prod", message.Title);
        Assert.Equal(1, _router.ToldCount);
    }

    [Fact]
    public void StartsOnOneSample_ShareOneMessage()
    {
        var message = Assert.Single(Route(Channel(), Started(Cpu()), Started(Blocking())));

        Assert.Equal(2, message.Lines.Count);
    }

    [Fact]
    public void AConnectionTheChannelDoesntTake_PostsNothing()
    {
        Assert.Empty(Route(Channel(only: Other), Started(Cpu())));
        Assert.Single(Route(Channel(only: [Other, Connection]), Started(Cpu())));
    }

    [Fact]
    public void ACriticalOnlyChannel_SkipsWarnings_ButTakesCriticalStarts()
    {
        Assert.Empty(Route(Channel(criticalOnly: true), Started(Cpu(HealthLevel.Warning))));
        Assert.Single(Route(Channel(criticalOnly: true), Started(Cpu(HealthLevel.Critical))));
    }

    [Fact]
    public void AWarningThatTurnsCritical_OnACriticalOnlyChannel_IsPostedAsANewCriticalAlert()
    {
        var channel = Channel(criticalOnly: true);
        var warning = Cpu(HealthLevel.Warning);
        Route(channel, Started(warning));

        var message = Assert.Single(Route(channel, Escalated(warning)));

        Assert.Equal("🔴 Critical: CPU on Sales-Prod", message.Title);
        Assert.Equal(1, _router.ToldCount);
    }

    [Fact]
    public void AWarningThatTurnsCritical_OnAChannelThatWasTold_SaysNowCritical()
    {
        var channel = Channel();
        var warning = Cpu(HealthLevel.Warning);
        Route(channel, Started(warning));

        Assert.Equal("🔴 Now critical: CPU on Sales-Prod", Assert.Single(Route(channel, Escalated(warning))).Title);
    }

    [Fact]
    public void AnAlertTheChannelMissed_ThatTurnsCritical_IsPostedAsNew()
    {
        // Started while the channel was off, or before it was added.
        var warning = Cpu(HealthLevel.Warning);

        Assert.Equal("🔴 Critical: CPU on Sales-Prod", Assert.Single(Route(Channel(), Escalated(warning))).Title);
    }

    [Fact]
    public void AnEscalationOfAnotherConnection_PostsNothing()
    {
        Assert.Empty(Route(Channel(only: Other), Escalated(Cpu(HealthLevel.Warning))));
    }

    [Fact]
    public void GettingWorse_PostsNothing()
    {
        var channel = Channel();
        var alert = Cpu();
        Route(channel, Started(alert));

        Assert.Empty(Route(channel, Worsened(alert)));
    }

    // ── Ends (#123) ───────────────────────────────────────────────────────────

    private static AlertChange Stopped(Alert alert) =>
        new(alert with { EndedUtc = alert.StartedUtc.AddMinutes(5), EndReason = AlertEndReason.MonitoringStopped }, AlertChangeKind.Ended);

    [Fact]
    public void BackToNormal_IsPosted_ForAnAlertTheChannelWasTold_AndIsForgotten()
    {
        var channel = Channel();
        var alert = Cpu();
        Route(channel, Started(alert));

        Assert.Equal("✅ Back to normal: CPU on Sales-Prod", Assert.Single(Route(channel, Ended(alert))).Title);
        Assert.Equal(0, _router.ToldCount);
    }

    [Fact]
    public void AnEnd_OfAnAlertTheChannelWasntTold_PostsNothing()
    {
        var other = Channel(only: Other);
        var alert = Cpu();
        Route(other, Started(alert));
        Assert.Empty(Route(other, Ended(alert)));

        var criticalOnly = Channel(criticalOnly: true);
        var warning = Blocking(HealthLevel.Warning);
        Route(criticalOnly, Started(warning));
        Assert.Empty(Route(criticalOnly, Ended(warning)));
    }

    [Fact]
    public void TheEndOfAnEscalation_OnACriticalOnlyChannel_IsPosted()
    {
        var channel = Channel(criticalOnly: true);
        var warning = Cpu(HealthLevel.Warning);
        Route(channel, Started(warning));
        Route(channel, Escalated(warning));

        Assert.Equal("✅ Back to normal: CPU on Sales-Prod", Assert.Single(Route(channel, Ended(warning))).Title);
    }

    [Fact]
    public void EndsFollowStarts_EvenWhenTheChannelsFiltersChangedSince()
    {
        var alert = Cpu();
        Route(Channel(), Started(alert));

        // Now for another connection only: it was told about the start, so it hears the end.
        Assert.Single(Route(Channel(only: Other), Ended(alert)));
    }

    [Fact]
    public void WithPostEndsOff_BackToNormalPostsNothing_ButIsStillForgotten()
    {
        var channel = Channel();
        channel.PostEnds = false;
        var alert = Cpu();
        Route(channel, Started(alert));

        Assert.Empty(Route(channel, Ended(alert)));
        Assert.Equal(0, _router.ToldCount);
    }

    [Fact]
    public void EndsOnOneSample_ShareOneMessage()
    {
        var channel = Channel();
        var (cpu, blocking) = (Cpu(), Blocking());
        Route(channel, Started(cpu), Started(blocking));

        var message = Assert.Single(Route(channel, Ended(cpu), Ended(blocking)));

        Assert.Equal("✅ 2 alerts back to normal on Sales-Prod", message.Title);
    }

    [Fact]
    public void MonitoringStopping_PostsOneMessage_ListingOnlyWhatTheChannelWasTold_WithNoBackToNormal()
    {
        var channel = Channel(criticalOnly: true);
        channel.PostEnds = false; // posted either way
        var (cpu, blocking, log) = (Cpu(), Blocking(HealthLevel.Warning), Log());
        Route(channel, Started(cpu), Started(blocking), Started(log));

        var message = Assert.Single(Route(channel, Stopped(cpu), Stopped(blocking), Stopped(log)));

        Assert.Equal("⏹ Stopped monitoring Sales-Prod", message.Title);
        Assert.Equal("SqlVitals on DBA-PC01 stopped monitoring Sales-Prod with 2 alerts still active (CPU, Log space used). " +
                     "No further updates will be posted.", message.Text);
        Assert.Equal(0, _router.ToldCount);
    }

    [Fact]
    public void MonitoringStopping_WithNothingTheChannelWasTold_PostsNothing()
    {
        var channel = Channel(criticalOnly: true);
        var warning = Blocking(HealthLevel.Warning);
        Route(channel, Started(warning));

        Assert.Empty(Route(channel, Stopped(warning)));
    }

    [Fact]
    public void AnEnd_OfARemovedConnection_UsesTheNameTheChannelWasToldWith()
    {
        var channel = Channel();
        var alert = Cpu();
        Route(channel, Started(alert));

        // Settings forget a removed connection's name before its session ends the alerts (F8).
        var message = Assert.Single(_router.Route(channel, Connection, null, [Stopped(alert)], "DBA-PC01"));

        Assert.Equal("⏹ Stopped monitoring Sales-Prod", message.Title);
    }

    [Fact]
    public void AtExit_StopsCollect_IntoOneMessageForEveryConnection()
    {
        var channel = Channel();
        var (sales, hr) = (Cpu(), Log() with { ConnectionId = Other });
        Route(channel, Started(sales));
        _router.Route(channel, Other, "HR-DB", [Started(hr)], "DBA-PC01");
        _router.CollectingStops = true;

        Assert.Empty(Route(channel, Stopped(sales)));
        Assert.Empty(_router.Route(channel, Other, "HR-DB", [Stopped(hr)], "DBA-PC01"));
        var message = _router.TakeStops("DBA-PC01");

        Assert.NotNull(message);
        Assert.Equal("⏹ Stopped monitoring 2 connections", message.Title);
        Assert.Equal("SqlVitals on DBA-PC01 stopped monitoring Sales-Prod (CPU) and HR-DB (Log space used), " +
                     "with alerts still active. No further updates will be posted.", message.Text);
        Assert.Null(_router.TakeStops("DBA-PC01"));
    }

    [Fact]
    public void AtExit_WithNothingTold_ThereIsNoStoppedMessage()
    {
        _router.CollectingStops = true;

        Assert.Empty(Route(Channel(), Stopped(Cpu())));
        Assert.Null(_router.TakeStops("DBA-PC01"));
    }

    [Fact]
    public void Forget_ClearsWhatTheChannelWasTold()
    {
        var channel = Channel();
        var warning = Cpu(HealthLevel.Warning);
        Route(channel, Started(warning));

        _router.Forget();

        Assert.Equal("🔴 Critical: CPU on Sales-Prod", Assert.Single(Route(channel, Escalated(warning))).Title);
    }

    [Fact]
    public void OnlyTheChangesThatPost_AreInTheMessage()
    {
        var channel = Channel(criticalOnly: true);
        var worse = Cpu();
        Route(channel, Started(worse));

        var message = Assert.Single(Route(channel, Worsened(worse), Started(Blocking(HealthLevel.Warning)), Started(Log())));

        Assert.Equal("🔴 Critical: Log space used on Sales-Prod", message.Title);
    }
}
