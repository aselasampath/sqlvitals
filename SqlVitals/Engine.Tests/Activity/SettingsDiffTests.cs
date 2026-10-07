using SqlVitals.Engine.Activity;

namespace SqlVitals.Engine.Tests.Activity;

public sealed class SettingsDiffTests
{
    internal const string Password   = "Pa55w0rd!s3cret";
    internal const string Parameters = "Server=sql01;User ID=sa;Password=Pa55w0rd!s3cret";
    internal const string Webhook    = "https://hooks.example.com/services/T000/B000/XXXXSECRET";

    private static readonly Guid     Id  = Guid.Parse("00000000-0000-4000-8000-000000000001");
    private static readonly DateTime Now = new(2026, 9, 29, 9, 0, 0, DateTimeKind.Utc);

    internal static SettingsSnapshot Snapshot(bool automatic = false, int timeout = 30, string password = Password,
                                              string parameters = Parameters, string webhook = Webhook,
                                              bool background = false, string name = "Prod", bool withConnection = true) =>
        new(
            [
                new("Start monitoring automatically", automatic ? "On" : "Off"),
                new("Command timeout", $"{timeout} s"),
                new("Webhook URL", webhook),
            ],
            withConnection
                ? [new ConnectionSnapshot(Id, name,
                   [
                       new("Name", name),
                       new("Server", "sql01"),
                       new("Password", password),
                       new("Additional parameters", parameters),
                       new("Monitor in background", background ? "On" : "Off"),
                   ])]
                : []);

    [Fact]
    public void NothingChanged_WritesNothing()
    {
        Assert.Empty(SettingsDiff.Between(Snapshot(), Snapshot(), ActivitySources.Settings, Now));
    }

    [Fact]
    public void OnlyTheSettingsThatChanged_AreLogged_WithOldAndNewValues()
    {
        var rows = SettingsDiff.Between(Snapshot(), Snapshot(automatic: true, timeout: 60), ActivitySources.Settings, Now);

        Assert.Collection(rows,
            r =>
            {
                Assert.Equal(ActivityEvents.SettingChanged, r.Event);
                Assert.Equal("Start monitoring automatically", r.Setting);
                Assert.Equal(("Off", "On"), (r.OldValue, r.NewValue));
                Assert.Equal(ActivitySources.Settings, r.Source);
                Assert.Null(r.ConnectionId);
                Assert.Equal(Now, r.TimeUtc);
            },
            r => Assert.Equal(("Command timeout", "30 s", "60 s"), (r.Setting, r.OldValue, r.NewValue)));
    }

    [Fact]
    public void ASecretSetting_OnlySaysItChanged()
    {
        var row = Assert.Single(SettingsDiff.Between(Snapshot(), Snapshot(webhook: "https://other.example.com/x"), ActivitySources.Settings, Now));

        Assert.Equal("Webhook URL", row.Setting);
        Assert.Null(row.OldValue);
        Assert.Null(row.NewValue);
        Assert.Equal(SettingsDiff.SecretChanged, row.Detail);
    }

    [Fact]
    public void AConnectionField_IsLoggedAgainstTheConnection_AndItsSecretsOnlyAsChanged()
    {
        var rows = SettingsDiff.Between(Snapshot(), Snapshot(password: "n3w", parameters: "Encrypt=True", background: true),
                                        ActivitySources.Settings, Now);

        Assert.All(rows, r => Assert.Equal((ActivityEvents.ConnectionEdited, (Guid?)Id, "Prod"), (r.Event, r.ConnectionId, r.ConnectionName)));
        Assert.Equal(new[] { "Password", "Additional parameters", "Monitor in background" }, rows.Select(r => r.Setting));
        Assert.All(rows.Take(2), r => Assert.Equal((null, null, "changed"), (r.OldValue, r.NewValue, r.Detail)));
        Assert.Equal(("Off", "On"), (rows[2].OldValue, rows[2].NewValue));
    }

    [Fact]
    public void ConnectionAddedAndRemoved_AreOneRowEach()
    {
        var added = Assert.Single(SettingsDiff.Between(Snapshot(withConnection: false), Snapshot(), ActivitySources.Settings, Now,
                                                       _ => "sql01 · Sales · SQL login"));
        Assert.Equal((ActivityEvents.ConnectionAdded, "Prod", "sql01 · Sales · SQL login"), (added.Event, added.ConnectionName, added.Detail));

        var removed = Assert.Single(SettingsDiff.Between(Snapshot(), Snapshot(withConnection: false), ActivitySources.Settings, Now));
        Assert.Equal((ActivityEvents.ConnectionRemoved, (Guid?)Id, "Prod"), (removed.Event, removed.ConnectionId, removed.ConnectionName));
    }

    [Fact]
    public void ARenamedConnection_IsLoggedUnderItsNewName()
    {
        var row = Assert.Single(SettingsDiff.Between(Snapshot(), Snapshot(name: "Production"), ActivitySources.Settings, Now));

        Assert.Equal(("Name", "Prod", "Production", "Production"), (row.Setting, row.OldValue, row.NewValue, row.ConnectionName));
    }

    [Fact]
    public void NoSecret_EverReachesARow()
    {
        var rows = SettingsDiff.Between(Snapshot(withConnection: false), Snapshot(), ActivitySources.Settings, Now)
            .Concat(SettingsDiff.Between(Snapshot(), Snapshot(password: "x", parameters: "y", webhook: "z"), ActivitySources.Settings, Now))
            .Concat(SettingsDiff.Between(Snapshot(password: "x", parameters: "y", webhook: "z"), Snapshot(), ActivitySources.Settings, Now))
            .Concat(SettingsDiff.Between(Snapshot(), Snapshot(withConnection: false), ActivitySources.Settings, Now))
            .ToList();

        Assert.NotEmpty(rows);
        foreach (var text in rows.SelectMany(Texts))
        {
            Assert.DoesNotContain(Password, text);
            Assert.DoesNotContain("Server=sql01", text);
            Assert.DoesNotContain("hooks.example.com", text);
        }
    }

    private static readonly Guid ChannelId = Guid.Parse("00000000-0000-4000-8000-000000000002");

    private static SettingsSnapshot WithChannel(string name = "DBA team", string webhook = Webhook, string on = "On", bool withChannel = true) =>
        Snapshot() with
        {
            ChatChannels = withChannel
                ? [new ConnectionSnapshot(ChannelId, name,
                   [
                       new("Name", name),
                       new("Service", "Slack"),
                       new("Webhook URL", webhook),
                       new("On", on),
                   ])]
                : [],
        };

    [Fact]
    public void AChatChannelAddedOrRemoved_IsOneRowNamingIt_WithoutItsUrl()
    {
        var added = Assert.Single(SettingsDiff.Between(WithChannel(withChannel: false), WithChannel(), ActivitySources.Settings, Now));
        Assert.Equal((ActivityEvents.ChatChannelAdded, "DBA team", null), (added.Event, added.Detail, added.ConnectionName));

        var removed = Assert.Single(SettingsDiff.Between(WithChannel(), WithChannel(withChannel: false), ActivitySources.Settings, Now));
        Assert.Equal((ActivityEvents.ChatChannelRemoved, "DBA team"), (removed.Event, removed.Detail));
    }

    [Fact]
    public void AChatChannelEdit_NamesTheChannelAndField_AndItsUrlOnlyAsChanged()
    {
        var rows = SettingsDiff.Between(WithChannel(), WithChannel(webhook: "https://hooks.example.com/services/NEW", on: "Off"),
                                        ActivitySources.Settings, Now);

        Assert.All(rows, r => Assert.Equal(ActivityEvents.ChatChannelEdited, r.Event));
        Assert.Collection(rows,
            r => Assert.Equal(("DBA team: Webhook URL", null, null, SettingsDiff.SecretChanged), (r.Setting, r.OldValue, r.NewValue, r.Detail)),
            r => Assert.Equal(("DBA team: On", "On", "Off"), (r.Setting, r.OldValue, r.NewValue)));
        Assert.All(rows.SelectMany(Texts), t => Assert.DoesNotContain("hooks.example.com", t));
    }

    [Fact]
    public void ARenamedChatChannel_IsLoggedUnderItsNewName()
    {
        var row = Assert.Single(SettingsDiff.Between(WithChannel(), WithChannel(name: "Ops"), ActivitySources.Settings, Now));

        Assert.Equal(("Ops: Name", "DBA team", "Ops"), (row.Setting, row.OldValue, row.NewValue));
    }

    [Theory]
    [InlineData("Password")]
    [InlineData("password")]
    [InlineData("Additional parameters")]
    [InlineData("Connection string")]
    [InlineData("Access token")]
    [InlineData("Webhook URL")]
    public void SecretNames_AreRecognisedWhateverTheCase(string name) => Assert.True(SettingsDiff.IsSecret(name));

    internal static IEnumerable<string> Texts(ActivityEntry e) =>
        new[] { e.Event, e.ConnectionName, e.Source, e.Setting, e.OldValue, e.NewValue, e.Detail, e.WindowsUser, e.Machine }
            .OfType<string>();
}
