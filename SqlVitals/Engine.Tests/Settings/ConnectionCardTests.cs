using System.Globalization;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Settings;
using SqlVitals.Engine.Tests.Home;

namespace SqlVitals.Engine.Tests.Settings;

/// <summary>A saved connection's card in Settings → Connections (#131): its label, tags and state.</summary>
public sealed class ConnectionCardTests
{
    private static ConnectionCardInput Input(
        string name = "UAT", bool active = false, bool background = false, bool own = false,
        bool monitored = false, HealthScore? score = null, string signIn = "SQL login") =>
        new(Guid.NewGuid(), name, " uat-sql01 ", "Sales", signIn, active, background, own, monitored, score);

    private static ConnectionCard Card(ConnectionCardInput input)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-GB");
        try { return ConnectionCard.From(input); }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void TheActiveConnection_IsLabelledActive_WithItsScore()
    {
        var card = Card(Input("Production", active: true, monitored: true, signIn: "Entra MFA",
                              score: ServerTileTests.Score(ServerTileTests.Sample(ple: 95))));

        Assert.Equal((ConnectionRole.Active, "ACTIVE"), (card.Role, card.RoleLabel));
        Assert.True(card.IsActive);
        Assert.Equal(["Entra MFA", "Monitored", "Default thresholds"], card.Tags);
        Assert.Equal((TileStatus.Warning, "Warning: page life expectancy 95 s · score 92"), (card.Status, card.StateText));
        Assert.Equal("uat-sql01 / Sales", card.ServerLine);
    }

    [Fact]
    public void AConnectionMonitoredInTheBackground_IsLabelledBackground()
    {
        var card = Card(Input(background: true, own: true, monitored: true,
                              score: ServerTileTests.Score(ServerTileTests.Sample(cpuPct: 81, blocked: 3, blockSec: 180))));

        Assert.Equal((ConnectionRole.Background, "BACKGROUND"), (card.Role, card.RoleLabel));
        Assert.Equal(["SQL login", "Monitor in background", "Own thresholds"], card.Tags);
        Assert.Equal((TileStatus.Critical, "Critical: blocking for 3 min (+1 more) · score 70"), (card.Status, card.StateText));
        Assert.Equal("UAT, background, critical: blocking for 3 min (+1 more) · score 70", card.AutomationName);
    }

    [Fact]
    public void AConnectionStartedByHand_WithoutMonitorInBackground_SaysSo()
    {
        var card = Card(Input(monitored: true, score: ServerTileTests.Score(ServerTileTests.Sample())));

        Assert.Equal(ConnectionRole.Background, card.Role);
        Assert.Equal("Started by you", card.Tags[1]);
        Assert.Equal("Healthy: every check passes · score 100", card.StateText);
    }

    [Fact]
    public void AConnectionThatCantBeReached_SaysSo_InsteadOfAScore()
    {
        var card = Card(Input("DR replica", background: true, monitored: true, signIn: "Windows auth",
                              score: HealthScore.Unavailable("Login failed.", DateTime.Now)));

        Assert.Equal(ConnectionRole.Background, card.Role);
        Assert.Equal((TileStatus.Unreachable, "Unreachable · can't connect"), (card.Status, card.StateText));
    }

    [Fact]
    public void AConnectionNotMonitored_IsOff_WithNoScore_EvenWithMonitorInBackground()
    {
        // Start monitoring automatically is off, so Monitor in background doesn't start it.
        var card = Card(Input("localhost\\SQLEXPRESS", background: true, signIn: "Windows auth"));

        Assert.Equal((ConnectionRole.Off, "OFF"), (card.Role, card.RoleLabel));
        Assert.Equal(["Windows auth", "Not monitored", "Default thresholds"], card.Tags);
        Assert.Equal((TileStatus.Off, "No score: not monitored"), (card.Status, card.StateText));
    }

    [Fact]
    public void TheActiveConnection_NotMonitored_IsStillActive_ButHasNoScore()
    {
        var card = Card(Input(active: true));

        Assert.Equal(ConnectionRole.Active, card.Role);
        Assert.Equal("Not monitored", card.Tags[1]);
        Assert.Equal("No score: not monitored", card.StateText);
    }

    [Fact]
    public void BeforeTheFirstSample_ItSaysItsWaiting()
    {
        var card = Card(Input(monitored: true));

        Assert.Equal((TileStatus.Waiting, "Waiting for the first sample"), (card.Status, card.StateText));
    }

    [Fact]
    public void Cards_AreInTheSidebarSelectorsOrder_ByName()
    {
        var cards = new[] { "uat", "Production", "DR replica", "reporting" }.Select(n => Card(Input(n)));

        Assert.Equal(["DR replica", "Production", "reporting", "uat"], ConnectionCard.Order(cards).Select(c => c.Name));
    }
}
