using Microsoft.Extensions.Logging;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Service;

namespace SqlVitals.Collector.Tests;

/// <summary>Real time: the collectors sample every 5 seconds, so these don't share the machine with other timing tests.</summary>
[CollectionDefinition(nameof(CollectorTiming), DisableParallelization = true)]
public sealed class CollectorTiming;

/// <summary>
/// The collector service's monitoring without the service (#172): it watches what collector.json
/// says, writes a history the app reads, tells the chat channels, and follows the file as it
/// changes. Real collectors on the real clock, fake servers and a fake webhook.
/// </summary>
[Collection(nameof(CollectorTiming))]
public sealed class CollectorHostTests : IDisposable
{
    private const string Webhook = "https://hooks.slack.com/services/T0001/B0002/abcdEFGHijklMNOPx7Qa";

    private readonly TempFolder  _folder  = new();
    private readonly FakeSender  _sender  = new();
    private readonly ListLogger  _log     = new();
    private readonly Dictionary<Guid, FakeServer> _servers = new();

    public void Dispose() => _folder.Dispose();

    private CollectorServer Server(string name, double cpu = 12)
    {
        var server = new CollectorServer { Name = name, Server = name.ToLowerInvariant() };
        _servers[server.Id] = new FakeServer(() => FakeServer.Snapshot(cpu));
        return server;
    }

    private static CollectorSettings Settings(IEnumerable<CollectorServer> servers, params ChatChannel[] channels) =>
        new() { SampleSeconds = 5, AlertSamples = 1, Servers = servers.ToList(), ChatChannels = channels.ToList() };

    private static ChatChannel Channel() => new() { Name = "DBA team", Service = ChatService.Slack, WebhookUrl = Webhook };

    private void Save(CollectorSettings settings) =>
        CollectorSettingsFile.Save(CollectorPaths.SettingsFile(_folder.Path), settings, new FakeProtector());

    private CollectorHost Host() =>
        new(_folder.Path, new FakeProtector(), (server, _) => new ServerRepositories(_servers[server.Id], _servers[server.Id]),
            _sender, _log);

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Timed out waiting for {what}.");
            await Task.Delay(25);
        }
    }

    private IReadOnlyList<ActivityEntry> Activity() =>
        new HistoryReader(CollectorPaths.HistoryFile(_folder.Path))
            .ReadActivity(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1))
            .Select(a => a.Entry).Reverse().ToList();

    [Fact]
    public async Task HistoryWrittenByTheHost_IsReadByTheApp()
    {
        var a = Server("Sales-Prod");
        Save(Settings([a]));

        using (var host = Host())
        {
            await host.StartAsync();
            Assert.Equal([a.Id], await host.WatchingAsync());
            // The first sample has no rates to save; the second does.
            await WaitUntil(() => _servers[a.Id].Reads >= 2, "two samples");
            await host.StopAsync();
        }

        // The app's own reader, on the service's file: the same schema.
        var reader = new HistoryReader(CollectorPaths.HistoryFile(_folder.Path));
        var from = DateTime.UtcNow.AddHours(-1);
        var to   = DateTime.UtcNow.AddHours(1);
        Assert.NotEmpty(reader.ReadMetrics(a.Id, from, to, TimeSpan.FromSeconds(1)));
        Assert.True(reader.ReadScores(a.Id, from, to, TimeSpan.FromSeconds(1)).Count >= 2);
        Assert.NotNull(reader.ReadLastSampleTime(a.Id));

        var activity = Activity();
        Assert.Equal([ActivityEvents.AppStarted, ActivityEvents.MonitoringStarted, ActivityEvents.MonitoringStopped, ActivityEvents.AppExited],
                     activity.Select(e => e.Event));
        Assert.All(activity, e => Assert.Equal(ActivitySources.CollectorService, e.Source));
        Assert.All(activity, e => Assert.Equal(Environment.MachineName, e.Machine));
        Assert.StartsWith("SqlVitals Collector ", activity[0].Detail);
        Assert.Equal("Sales-Prod", activity[1].ConnectionName);
    }

    [Fact]
    public async Task Stopping_TellsEachChannel_TheServiceStopped()
    {
        var a = Server("Sales-Prod");
        Save(Settings([a], Channel()));

        using var host = Host();
        await host.StartAsync();
        await WaitUntil(() => !_sender.Sent.IsEmpty, "the watching message");
        await host.StopAsync();

        var texts = _sender.Texts;
        Assert.Contains("watching Sales-Prod", texts[0]);
        Assert.Contains($"SqlVitals on {CollectorHost.PcName}", texts[0]);
        Assert.Contains("stopped watching Sales-Prod because the SqlVitals Collector service stopped", texts[^1]);
    }

    [Fact]
    public async Task Alerts_ReachTheChannels_AndEndWithTheService()
    {
        var a = Server("Sales-Prod", cpu: 99);
        Save(Settings([a], Channel()));

        using var host = Host();
        await host.StartAsync();
        await WaitUntil(() => _sender.Texts.Any(t => t.Contains("CPU", StringComparison.Ordinal)), "the CPU alert");
        await host.StopAsync();

        Assert.Contains(_sender.Texts, t => t.Contains("CPU", StringComparison.Ordinal) && t.Contains("Sales-Prod", StringComparison.Ordinal));
        // The alert still active when it stopped is in the stopped message, not posted as back to normal.
        Assert.Contains("1 alert still active", _sender.Texts[^1]);
        var alerts = new HistoryReader(CollectorPaths.HistoryFile(_folder.Path)).ReadAlerts(a.Id, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));
        Assert.Single(alerts);
        Assert.NotNull(alerts[0].Alert.EndedUtc);
    }

    [Fact]
    public async Task ASettingsChange_StartsAndStopsServers_WithoutARestart()
    {
        var a = Server("Sales-Prod");
        var b = Server("HR-DB");
        Save(Settings([a], Channel()));

        using var host = Host();
        await host.StartAsync();
        await WaitUntil(() => _servers[a.Id].Reads >= 1, "a sample of Sales-Prod");

        Save(Settings([b], Channel()));
        await host.CheckSettingsNowAsync();
        Assert.Equal([b.Id], await host.WatchingAsync());
        await WaitUntil(() => _servers[b.Id].Reads >= 1, "a sample of HR-DB");
        await WaitUntil(() => _sender.Texts.Any(t => t.Contains("stopped watching Sales-Prod", StringComparison.Ordinal)), "the stopped message");
        await host.StopAsync();

        Assert.Contains(_sender.Texts, t => t.Contains("stopped watching Sales-Prod because the connection was removed", StringComparison.Ordinal));
        Assert.Contains(_sender.Texts, t => t.Contains("watching HR-DB", StringComparison.Ordinal));
        var changed = Activity().Where(e => e.Source == ActivitySources.CollectorSettings).ToList();
        Assert.Contains(changed, e => e.Event == ActivityEvents.MonitoringStopped && e.ConnectionId == a.Id);
        Assert.Contains(changed, e => e.Event == ActivityEvents.MonitoringStarted && e.ConnectionId == b.Id);
    }

    [Fact]
    public async Task AnEditedServer_IsRestarted_WithoutTellingTheChannelsItStopped()
    {
        var a = Server("Sales-Prod");
        Save(Settings([a], Channel()));

        using var host = Host();
        await host.StartAsync();
        await WaitUntil(() => !_sender.Sent.IsEmpty, "the watching message");

        a.Database = "Sales";
        Save(Settings([a], Channel()));
        await host.CheckSettingsNowAsync();
        Assert.Equal([a.Id], await host.WatchingAsync());
        await host.StopAsync();

        // Watching, then stopped at the end: nothing about the edit in between.
        Assert.Equal(2, _sender.Texts.Count);
        Assert.Contains(Activity(), e => e.Event == ActivityEvents.MonitoringStarted && e.Source == ActivitySources.ConnectionEdited);
    }

    [Fact]
    public async Task ASettingsFileThatCantBeRead_FailsTheStart()
    {
        await File.WriteAllTextAsync(CollectorPaths.SettingsFile(_folder.Path), "{ not json");

        using var host = Host();
        var ex = await Assert.ThrowsAsync<CollectorSettingsException>(host.StartAsync);
        Assert.Contains("couldn't be read", ex.Message);
    }

    [Fact]
    public async Task ASettingsFileBrokenLater_KeepsWatchingWhatRuns()
    {
        var a = Server("Sales-Prod");
        Save(Settings([a]));

        using var host = Host();
        await host.StartAsync();
        await File.WriteAllTextAsync(CollectorPaths.SettingsFile(_folder.Path), "{ not json, and longer than before");
        await host.CheckSettingsNowAsync();

        Assert.Equal([a.Id], await host.WatchingAsync());
        Assert.Contains(_log.Lines, l => l.Level == LogLevel.Error && l.Text.Contains("stay as they were", StringComparison.Ordinal));
        await host.StopAsync();
    }

    [Fact]
    public async Task ABrokenFile_IsSaidOnce_NotAtEveryCheck()
    {
        Save(Settings([Server("Sales-Prod")]));
        using var host = Host();
        await host.StartAsync();

        await File.WriteAllTextAsync(CollectorPaths.SettingsFile(_folder.Path), "{ not json, and longer than before");
        await host.CheckSettingsNowAsync();
        await host.CheckSettingsNowAsync();
        await host.CheckSettingsNowAsync();

        Assert.Single(_log.Lines, l => l.Level == LogLevel.Error);
        await host.StopAsync();
    }

    [Fact]
    public async Task AServerWhoseExtraParametersArentAConnectionString_IsntWatched_TheOthersAre()
    {
        var bad  = Server("Sales-Prod");
        bad.AdditionalParameters = "Pooling=perhaps";
        var good = Server("HR-DB");
        Save(Settings([bad, good]));

        using var host = Host();
        await host.StartAsync();

        Assert.Equal([good.Id], await host.WatchingAsync());
        Assert.Contains(_log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("Sales-Prod: its additionalParameters", StringComparison.Ordinal));
        await host.StopAsync();
    }

    [Fact]
    public void TheLog_GetsNoSecret_WhicheverProviderWritesIt()
    {
        var ex = new InvalidOperationException("Login failed: Server=sql01;User ID=sa;Password=s3cret;");

        _log.Error("Collector: couldn't read Sales-Prod", ex);
        _log.Warning($"Posting to {Webhook} failed");

        Assert.All(_log.Lines, l => Assert.DoesNotContain("s3cret", l.Text));
        Assert.All(_log.Lines, l => Assert.DoesNotContain("abcdEFGH", l.Text));
        Assert.Contains("couldn't read Sales-Prod", _log.Lines.First().Text);
    }

    [Fact]
    public async Task ASqlLoginWithoutAPassword_IsntWatched_AndTheLogSaysHowToFixIt()
    {
        var a = Server("Sales-Prod");
        a.Authentication = CollectorAuth.SqlLogin;
        a.UserName       = "vitals";
        Save(Settings([a]));

        using var host = Host();
        await host.StartAsync();

        Assert.Empty(await host.WatchingAsync());
        Assert.Contains(_log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("set-password \"Sales-Prod\"", StringComparison.Ordinal));
        await host.StopAsync();
    }

    [Fact]
    public async Task NoSettingsFile_WatchesNothing_AndSaysHowToAddAServer()
    {
        using var host = Host();
        await host.StartAsync();

        Assert.Empty(await host.WatchingAsync());
        Assert.Contains(_log.Lines, l => l.Text.Contains("add-server", StringComparison.Ordinal));
        await host.StopAsync();
    }
}
