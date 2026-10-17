using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Tests;

/// <summary>A connection's page as typed (#131): what's unsaved, on which tabs, and what needs the server.</summary>
public sealed class ConnectionDraftTests
{
    private static readonly ConnectionDraft Saved = new(
        Name: "UAT", Server: "uat-sql01", Authentication: SqlAuthMode.SqlServer, UserName: "monitor", Password: "secret",
        SavePassword: true, Database: "Sales", Encrypt: EncryptMode.Mandatory, TrustServerCertificate: false,
        ConnectTimeout: "30", AdditionalParameters: "", MonitorInBackground: false, Notifies: true,
        OwnThresholds: false, Thresholds: "");

    [Fact]
    public void TheSameValues_AreNothingUnsaved()
    {
        Assert.Equal(Saved, Saved with { });
        Assert.Empty((Saved with { }).ChangedTabs(Saved));
    }

    [Fact]
    public void EachChange_IsOnItsTab()
    {
        Assert.Equal([ConnectionTab.Connection], (Saved with { Name = "UAT 2" }).ChangedTabs(Saved));
        Assert.Equal([ConnectionTab.Monitoring], (Saved with { Notifies = false }).ChangedTabs(Saved));
        Assert.Equal([ConnectionTab.Thresholds], (Saved with { OwnThresholds = true, Thresholds = "85;95" }).ChangedTabs(Saved));
        Assert.Equal([ConnectionTab.Advanced], (Saved with { ConnectTimeout = "60" }).ChangedTabs(Saved));
        Assert.Equal([ConnectionTab.Connection, ConnectionTab.Advanced],
                     (Saved with { Server = "uat-sql02", AdditionalParameters = "MultipleActiveResultSets=True" }).ChangedTabs(Saved));
    }

    [Fact]
    public void HowItConnects_IsCheckedAgainstTheServer()
    {
        Assert.True((Saved with { Server = "uat-sql02" }).NeedsServerCheck(Saved));
        Assert.True((Saved with { Password = "new" }).NeedsServerCheck(Saved));
        Assert.True((Saved with { Authentication = SqlAuthMode.Windows }).NeedsServerCheck(Saved));
        Assert.True((Saved with { Encrypt = EncryptMode.Strict }).NeedsServerCheck(Saved));
        Assert.True((Saved with { ConnectTimeout = "60" }).NeedsServerCheck(Saved));
    }

    [Fact]
    public void ANameOrAnAlertSetting_SavesWithoutTheServer_SoADownServerCanStillBeMuted()
    {
        Assert.False((Saved with { Name = "UAT 2" }).NeedsServerCheck(Saved));
        Assert.False((Saved with { Notifies = false, MonitorInBackground = true }).NeedsServerCheck(Saved));
        Assert.False((Saved with { OwnThresholds = true, Thresholds = "85;95" }).NeedsServerCheck(Saved));
        Assert.False((Saved with { SavePassword = false }).NeedsServerCheck(Saved));
    }
}
