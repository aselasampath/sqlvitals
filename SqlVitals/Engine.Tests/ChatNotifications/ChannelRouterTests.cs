using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Monitoring;
using static SqlVitals.Engine.Tests.ChatNotifications.AlertMessageTests;

namespace SqlVitals.Engine.Tests.ChatNotifications;

/// <summary>What a channel is told about a connection's alert changes (#121).</summary>
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

    [Fact]
    public void AnEnd_PostsNothingYet_AndIsForgotten()
    {
        var channel = Channel();
        var alert = Cpu();
        Route(channel, Started(alert));

        Assert.Empty(Route(channel, Ended(alert)));
        Assert.Equal(0, _router.ToldCount);
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
