using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Service;

namespace SqlVitals.Engine.Tests.Service;

/// <summary>
/// collector.json (#172): readable, with every secret only in its protected form; what can't be
/// decrypted on this PC is said, never used; out-of-range values are brought back into range.
/// </summary>
public sealed class CollectorSettingsFileTests : IDisposable
{
    private const string Webhook  = "https://hooks.slack.com/services/T0001/B0002/abcdEFGHijklMNOPx7Qa";
    private const string Ping     = "https://hc-ping.com/5f0c7a3e-1b2d-4c5e-9f00-123456789abc";
    private const string Password = "Pa55;word=\"x\"";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "collector.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Reverses the text: not the text, and undone only by itself.</summary>
    private sealed class Reverser : ISecretProtector
    {
        public string Protect(string plainText) => "r:" + Reversed(plainText);

        public string Unprotect(string protectedText) =>
            protectedText.StartsWith("r:", StringComparison.Ordinal)
                ? Reversed(protectedText[2..])
                : throw new CryptographicException("Not protected on this PC.");
    }

    private static string Reversed(string text) => new(text.Reverse().ToArray());

    private static CollectorSettings Settings()
    {
        var sql = new CollectorServer { Name = "Sales-Prod", Server = "sql01", Database = "Sales", Authentication = CollectorAuth.SqlLogin, UserName = "vitals", Password = Password };
        var win = new CollectorServer { Server = "sql02" };
        return new CollectorSettings
        {
            SampleSeconds = 30,
            Servers       = [sql, win],
            ChatChannels  = [new ChatChannel
            {
                Name = "DBA team", WebhookUrl = Webhook, HeartbeatPingUrl = Ping, AllConnections = false, ConnectionIds = [sql.Id],
                HeartbeatEvery = HeartbeatEvery.Hours, HeartbeatHours = 6,
            }],
        };
    }

    [Fact]
    public void Saved_ThenLoaded_GivesTheSameSettings_WithEverySecretProtectedOnDisk()
    {
        var saved = Settings();
        CollectorSettingsFile.Save(FilePath, saved, new Reverser());

        var text = File.ReadAllText(FilePath);
        Assert.DoesNotContain(Password, text);
        Assert.DoesNotContain("abcdEFGH", text);
        Assert.DoesNotContain("hc-ping.com", text);
        Assert.Equal(3, text.Split(CollectorSettingsFile.ProtectedPrefix).Length - 1);
        Assert.Contains("\"server\": \"sql01\"", text);           // readable by an admin
        Assert.Contains("\"authentication\": \"SqlLogin\"", text);

        var (loaded, problems) = CollectorSettingsFile.Load(FilePath, new Reverser());
        Assert.Empty(problems);
        Assert.Equal(30, loaded.SampleSeconds);
        Assert.Equal(saved.Servers.Select(s => (s.Id, s.Server, s.Password)), loaded.Servers.Select(s => (s.Id, s.Server, s.Password)));
        var channel = Assert.Single(loaded.ChatChannels);
        Assert.Equal((Webhook, Ping, 6, false), (channel.WebhookUrl, channel.HeartbeatPingUrl, channel.HeartbeatHours, channel.AllConnections));
        Assert.Equal([saved.Servers[0].Id], channel.ConnectionIds);
    }

    [Fact]
    public void SecretsFromAnotherPc_OrTypedInClear_AreSaidAndNotUsed()
    {
        CollectorSettingsFile.Save(FilePath, Settings(), new Reverser());
        // The password and ping from another PC; the webhook typed into the file by hand.
        var text = File.ReadAllText(FilePath).Replace("dpapi:r:", "dpapi:other:", StringComparison.Ordinal);
        text = System.Text.RegularExpressions.Regex.Replace(text, "\"webhookUrl\": \"[^\"]*\"", $"\"webhookUrl\": \"{Webhook}\"");
        File.WriteAllText(FilePath, text);

        var (loaded, problems) = CollectorSettingsFile.Load(FilePath, new Reverser());

        var server = loaded.Servers[0];
        Assert.True(server.PasswordUnreadable);
        Assert.True(server.NeedsPassword);
        Assert.Empty(loaded.ChatChannels);
        Assert.Equal(2, problems.Count);
        Assert.Contains("set-password \"Sales-Prod\"", problems[0]);
        Assert.Contains("Chat \"DBA team\"", problems[1]);
        Assert.All(problems, p => Assert.DoesNotContain("abcdEFGH", p));
    }

    [Fact]
    public void NoFile_IsEmptySettings_AndOneThatCantBeRead_SaysWhereWithoutItsContents()
    {
        var (empty, problems) = CollectorSettingsFile.Load(FilePath, new Reverser());
        Assert.Empty(empty.Servers);
        Assert.Empty(problems);

        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "{ \"servers\": [ { \"password\": \"Pa55word\" ");
        var ex = Assert.Throws<CollectorSettingsException>(() => CollectorSettingsFile.Load(FilePath, new Reverser()));
        Assert.Contains(FilePath, ex.Message);
        Assert.DoesNotContain("Pa55word", ex.Message);
    }

    [Fact]
    public void ValuesOutOfRange_AreBroughtIntoRange_AndChannelsForgetServersThatAreGone()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, $$"""
            {
              // Edited by hand.
              "sampleSeconds": 1, "historyRetentionDays": 1000, "historyIntervalMinutes": 4, "alertSamples": 0, "commandTimeoutSeconds": -5,
              "servers": [ { "server": "sql01", "id": "00000000-0000-0000-0000-000000000000" } ],
              "chatChannels": [ { "name": "DBA", "webhookUrl": "dpapi:r:{{Reversed(Webhook)}}", "allConnections": false,
                                  "connectionIds": [ "{{Guid.NewGuid()}}" ] } ],
            }
            """);

        var settings = CollectorSettingsFile.Load(FilePath, new Reverser()).Settings;

        Assert.Equal((CollectorSettings.DefaultSampleSeconds, 90, 5, 3, 30),
                     (settings.SampleSeconds, settings.HistoryRetentionDays, settings.HistoryIntervalMinutes, settings.AlertSamples, settings.CommandTimeoutSeconds));
        Assert.NotEqual(Guid.Empty, settings.Servers[0].Id);
        Assert.Empty(settings.ChatChannels[0].ConnectionIds);
    }

    [Fact]
    public void TwoServersWithOneId_IsSaid_AndAServerTypedWithoutAnId_KeepsTheSameOneAtEveryRead()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{ "servers": [ { "server": "sql01" }, { "server": "sql02", "database": "HR" } ] }""");

        var first  = CollectorSettingsFile.Load(FilePath, new Reverser()).Settings.Servers.Select(s => s.Id).ToList();
        var second = CollectorSettingsFile.Load(FilePath, new Reverser()).Settings.Servers.Select(s => s.Id).ToList();
        Assert.Equal(first, second);
        Assert.NotEqual(first[0], first[1]);

        var id = Guid.NewGuid();
        File.WriteAllText(FilePath, $$"""{ "servers": [ { "server": "sql01", "id": "{{id}}" }, { "server": "sql02", "id": "{{id}}" } ] }""");
        var ex = Assert.Throws<CollectorSettingsException>(() => CollectorSettingsFile.Load(FilePath, new Reverser()));
        Assert.Contains($"2 servers with the id {id}", ex.Message);
    }

    [Fact]
    public void ExtraParametersThatArentAConnectionString_MakeTheServerUnusable_WithoutTheirValue()
    {
        var settings = Settings();
        settings.Servers[1].AdditionalParameters = "Password=hunter2;Pooling=perhaps";
        CollectorSettingsFile.Save(FilePath, settings, new Reverser());

        var (loaded, problems) = CollectorSettingsFile.Load(FilePath, new Reverser());

        Assert.NotNull(loaded.Servers[1].Unusable);
        Assert.Null(loaded.Servers[0].Unusable);
        Assert.Contains(problems, p => p.StartsWith("sql02: its additionalParameters", StringComparison.Ordinal));
        Assert.All(problems, p => Assert.DoesNotContain("hunter2", p));
    }

    [Fact]
    public void AServer_ConnectsAsTheCollector_WithTheFieldsWinningOverTheExtraParameters()
    {
        var windows = new SqlConnectionStringBuilder(new CollectorServer
        {
            Server = " sql01 ", Database = "Sales", Encrypt = CollectorEncrypt.Strict,
            AdditionalParameters = "Data Source=elsewhere;MultiSubnetFailover=True;Application Name=Other",
        }.BuildConnectionString());
        Assert.Equal(("sql01", "Sales", true, true, "SqlVitals Collector", SqlConnectionEncryptOption.Strict),
                     (windows.DataSource, windows.InitialCatalog, windows.IntegratedSecurity, windows.MultiSubnetFailover, windows.ApplicationName, windows.Encrypt));

        var login = new CollectorServer { Server = "sql01", Authentication = CollectorAuth.SqlLogin, UserName = "vitals", Password = Password };
        var sql   = new SqlConnectionStringBuilder(login.BuildConnectionString());
        Assert.Equal(("vitals", Password, false), (sql.UserID, sql.Password, sql.IntegratedSecurity));
        Assert.DoesNotContain(Password, login.ToString());
        Assert.Equal(2000, login.Target(0).LockTimeoutMilliseconds);
        Assert.NotEqual(login.Fingerprint(30), login.Fingerprint(60));
    }

    [Theory]
    [InlineData("", @"C:\Data\share\history-share.db")]
    [InlineData("share2", @"C:\Data\share2\history-share.db")]
    [InlineData(@"D:\Vitals", @"D:\Vitals\history-share.db")]
    public void TheShareCopy_IsInTheDataFolder_UnlessAFullPathIsGiven(string folder, string path) =>
        Assert.Equal(path, new CollectorSettings { ShareCopyFolder = folder }.ShareCopyPath(@"C:\Data"));

    [Fact]
    public void ServersAndChannels_AreFoundByNameServerOrId_IgnoringCase()
    {
        var settings = Settings();

        Assert.Same(settings.Servers[0], settings.FindServer("sales-prod"));
        Assert.Same(settings.Servers[1], settings.FindServer("SQL02"));
        Assert.Same(settings.Servers[1], settings.FindServer(settings.Servers[1].Id.ToString()));
        Assert.Null(settings.FindServer("sql03"));
        Assert.Same(settings.ChatChannels[0], settings.FindChannel(" dba TEAM "));
    }
}
