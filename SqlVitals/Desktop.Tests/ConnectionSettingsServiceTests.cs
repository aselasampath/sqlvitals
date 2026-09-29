using System.IO;
using System.Text.Json.Nodes;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;

namespace SqlVitals.Desktop.Tests;

/// <summary>Start monitoring automatically in settings.dat (#115), and the activity rows a save raises.</summary>
public sealed class ConnectionSettingsServiceTests : IDisposable
{
    private const string Secret = "Pa55w0rd!s3cret";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private string SettingsFile => Path.Combine(_dir, "settings.dat");

    private ConnectionSettingsService Create() => new(_dir);

    internal static ConnectionSettings Connection(string name = "Prod") => new()
    {
        Name         = name,
        Server       = "sql01",
        UserName     = "monitor",
        Password     = Secret,
        SavePassword = true,
        Database     = "Sales",
    };

    [Fact]
    public void ANewInstallation_ReadsAsOff()
    {
        Assert.False(Create().Load().StartMonitoringAutomatically);
    }

    [Fact]
    public void ANewConnection_HasMonitorInBackgroundOff()
    {
        Assert.False(new ConnectionSettings().MonitorInBackground);
    }

    [Fact]
    public void TheSetting_SurvivesSaveAndLoad()
    {
        var service = Create();
        service.Upsert(Connection());
        Assert.False(Create().Load().StartMonitoringAutomatically);   // saved explicitly as off

        service.SetStartMonitoringAutomatically(true);
        Assert.True(Create().Load().StartMonitoringAutomatically);

        service.SetStartMonitoringAutomatically(false);
        Assert.False(Create().Load().StartMonitoringAutomatically);
    }

    [Fact]
    public void AnUpgradedFileWithConnectionsAndNoValue_ReadsAsOn_AndKeepsEachConnectionsBox()
    {
        var service = Create();
        var background = Connection("Background");
        background.MonitorInBackground = true;
        service.Upsert(Connection());
        service.Upsert(background);
        RemoveSetting();

        var store = Create().Load();

        Assert.True(store.StartMonitoringAutomatically);
        Assert.False(store.Connections.Single(c => c.Name == "Prod").MonitorInBackground);
        Assert.True(store.Connections.Single(c => c.Name == "Background").MonitorInBackground);
    }

    [Fact]
    public void AnUpgradedFileWithoutConnections_ReadsAsOff()
    {
        Create().SetKeepRunningInTray(false);
        RemoveSetting();

        Assert.False(Create().Load().StartMonitoringAutomatically);
    }

    [Fact]
    public void AnUpgradedFile_IsSavedWithTheValue_SoItStaysOn()
    {
        Create().Upsert(Connection());
        RemoveSetting();

        Create().SetKeepRunningInTray(false);   // any save

        var json = JsonNode.Parse(File.ReadAllText(SettingsFile))!;
        Assert.True(json["StartMonitoringAutomatically"]!.GetValue<bool>());
    }

    [Fact]
    public void ASaveThatChangesNothing_RaisesNoRows()
    {
        var service = Create();
        service.Upsert(Connection());
        var raised = new List<ActivityEntry>();
        service.Changed += raised.AddRange;

        service.SetKeepRunningInTray(service.Load().KeepRunningInTray);
        service.SetStartMonitoringAutomatically(false);
        service.Upsert(service.Load().Connections[0]);

        Assert.Empty(raised);
    }

    [Fact]
    public void Saves_RaiseARowPerChange_WithTheirSource()
    {
        var service = Create();
        var raised  = new List<ActivityEntry>();
        service.Changed += raised.AddRange;

        var conn = Connection();
        service.Upsert(conn);
        service.SetStartMonitoringAutomatically(true);
        service.SetNotificationsMuted(conn.Id, muted: true, ActivitySources.TrayMenu);
        service.SetBackupRpo(new Engine.Backups.BackupRpo(TimeSpan.FromHours(48), TimeSpan.FromMinutes(30)));
        service.SetActive(conn.Id);   // switching isn't a setting
        service.Remove(conn.Id);

        Assert.Collection(raised,
            r => Assert.Equal((ActivityEvents.ConnectionAdded, "Prod", "sql01 · Sales · SQL login"), (r.Event, r.ConnectionName, r.Detail)),
            r => Assert.Equal(("Start monitoring automatically", "Off", "On", ActivitySources.Settings), (r.Setting, r.OldValue, r.NewValue, r.Source)),
            r => Assert.Equal((ActivityEvents.ConnectionEdited, "Windows notifications", "On", "Muted", ActivitySources.TrayMenu),
                              (r.Event, r.Setting, r.OldValue, r.NewValue, r.Source)),
            r => Assert.Equal(("Backup RPO (full)", "user: Backups"), (r.Setting, r.Source)),
            r => Assert.Equal(("Backup RPO (log)", "user: Backups"), (r.Setting, r.Source)),
            r => Assert.Equal((ActivityEvents.ConnectionRemoved, "Prod"), (r.Event, r.ConnectionName)));
    }

    [Fact]
    public void AnEditedPassword_OnlySaysItChanged_AndNoSecretIsEverRaised()
    {
        var service = Create();
        var raised  = new List<ActivityEntry>();
        service.Changed += raised.AddRange;

        var conn = Connection();
        conn.AdditionalParameters = "MultipleActiveResultSets=True";
        service.Upsert(conn);
        var edited = conn.Clone();
        edited.Password             = "An0therS3cret";
        edited.AdditionalParameters = "ApplicationIntent=ReadOnly";
        service.Upsert(edited);

        var password = raised.Single(r => r.Setting == "Password");
        Assert.Equal((null, null, SettingsDiff.SecretChanged), (password.OldValue, password.NewValue, password.Detail));
        foreach (var text in raised.SelectMany(r => new[] { r.ConnectionName, r.Setting, r.OldValue, r.NewValue, r.Detail }).OfType<string>())
        {
            Assert.DoesNotContain(Secret, text);
            Assert.DoesNotContain("An0therS3cret", text);
            Assert.DoesNotContain("MultipleActiveResultSets", text);
            Assert.DoesNotContain("ApplicationIntent", text);
        }
    }

    // As a settings.dat written before #115: the same file without the new value.
    private void RemoveSetting()
    {
        var json = JsonNode.Parse(File.ReadAllText(SettingsFile))!.AsObject();
        Assert.True(json.Remove("StartMonitoringAutomatically"));
        File.WriteAllText(SettingsFile, json.ToJsonString());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
