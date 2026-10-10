using System.Security.Principal;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Service;

namespace SqlVitals.Collector.Tests;

/// <summary>The command line (#172): what each command writes to collector.json, and that no secret is ever echoed.</summary>
public sealed class CommandsTests : IDisposable
{
    private const string Webhook = "https://hooks.slack.com/services/T0001/B0002/abcdEFGHijklMNOPx7Qa";
    private const string Ping    = "https://hc-ping.com/5f0c7a3e-1b2d-4c5e-9f00-123456789abc";

    private readonly TempFolder _folder = new();

    public void Dispose() => _folder.Dispose();

    /// <summary>Standard input from a list; the hidden prompt answers with <see cref="Typed"/>.</summary>
    private sealed class FakeConsole(params string[] stdin) : ICommandConsole
    {
        private readonly Queue<string> _stdin = new(stdin);

        public string? Typed { get; init; }

        public TextWriter Out   { get; } = new StringWriter();
        public TextWriter Error { get; } = new StringWriter();

        public string? ReadLine() => _stdin.TryDequeue(out var line) ? line : null;

        public string? ReadSecret(string prompt) => Typed;

        public string Text => Out + "\n" + Error;
    }

    private async Task<(int Code, FakeConsole Console)> Run(string line, FakeConsole? console = null)
    {
        console ??= new FakeConsole();
        var args = System.Text.RegularExpressions.Regex.Matches(line, "\"([^\"]*)\"|(\\S+)")
                         .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).ToArray();
        var code = await new Commands(console, _folder.Path, true, new FakeProtector(), new FakeWindows(),
                                      sender: () => new FakeSender()).RunAsync(CommandLine.Parse(args));
        return (code, console);
    }

    private CollectorSettings Saved() =>
        CollectorSettingsFile.Load(CollectorPaths.SettingsFile(_folder.Path), new FakeProtector()).Settings;

    private string FileText() => File.ReadAllText(CollectorPaths.SettingsFile(_folder.Path));

    [Fact]
    public async Task AddServer_WithWindowsAuthentication_SavesNoSecret()
    {
        var (code, console) = await Run("add-server --server sql01 --database Sales --name Sales-Prod --encrypt strict");

        Assert.Equal(0, code);
        var server = Assert.Single(Saved().Servers);
        Assert.Equal(("sql01", "Sales", "Sales-Prod", CollectorAuth.Windows, CollectorEncrypt.Strict),
                     (server.Server, server.Database, server.Name, server.Authentication, server.Encrypt));
        Assert.Contains("Added Sales-Prod", console.Text);
        Assert.Contains("applies when the service starts", console.Text);
    }

    [Fact]
    public async Task AddServer_WithASqlLogin_TakesThePasswordFromStdin_AndOnlyProtected()
    {
        var (code, console) = await Run("add-server --server sql01 --sql-login vitals --password-stdin", new FakeConsole("Pa55;word!"));

        Assert.Equal(0, code);
        var server = Assert.Single(Saved().Servers);
        Assert.Equal(("vitals", "Pa55;word!"), (server.UserName, server.Password));
        Assert.DoesNotContain("Pa55;word!", FileText());
        Assert.DoesNotContain("Pa55;word!", console.Text);
    }

    [Fact]
    public async Task AddServer_WithASqlLogin_AndNoConsole_SaysToPipeIt()
    {
        var (code, console) = await Run("add-server --server sql01 --sql-login vitals");

        Assert.Equal(2, code);
        Assert.Contains("--password-stdin", console.Text);
        Assert.False(File.Exists(CollectorPaths.SettingsFile(_folder.Path)));
    }

    [Theory]
    [InlineData("add-server --server SQL01", "already watches")]
    [InlineData("add-server --server sql02 --name sql01", "already a server called sql01")]
    [InlineData("add-server --database Sales", "--server is needed")]
    [InlineData("add-server --server sql02 --encrypt sometimes", "--encrypt must be one of mandatory, optional, strict")]
    [InlineData("add-server --server sql02 --id nope", "--id must be a GUID")]
    [InlineData("add-server --server sql02 --connect-timeout soon", "--connect-timeout must be a whole number")]
    public async Task AddServer_Refuses_WhatCantBeRight(string line, string says)
    {
        await Run("add-server --server sql01");

        var (code, console) = await Run(line);

        Assert.Equal(2, code);
        Assert.Contains(says, console.Text);
        Assert.Single(Saved().Servers);
    }

    [Fact]
    public async Task AddServer_KeepsTheIdItIsGiven_SoTheAppCanHandItsConnectionOver()
    {
        var id = Guid.NewGuid();

        await Run($"add-server --server sql01 --id {id}");

        Assert.Equal(id, Assert.Single(Saved().Servers).Id);
    }

    [Fact]
    public async Task RemoveServer_TakesItOutOfTheChannelsToo()
    {
        await Run("add-server --server sql01");
        await Run("add-server --server sql02");
        await Run("add-channel --name DBA --servers sql01,sql02 --stdin", new FakeConsole(Webhook));

        var (code, _) = await Run("remove-server sql01");

        Assert.Equal(0, code);
        var settings = Saved();
        Assert.Equal(["sql02"], settings.Servers.Select(s => s.Server));
        Assert.Equal([settings.Servers[0].Id], settings.ChatChannels[0].ConnectionIds);
    }

    [Fact]
    public async Task SetPassword_ReplacesIt_AndRefusesAWindowsServer()
    {
        await Run("add-server --server sql01 --sql-login vitals --password-stdin", new FakeConsole("old"));
        await Run("add-server --server sql02");

        Assert.Equal(0, (await Run("set-password sql01", new FakeConsole { Typed = "new" })).Code);
        Assert.Equal("new", Saved().Servers[0].Password);

        var (code, console) = await Run("set-password sql02", new FakeConsole { Typed = "x" });
        Assert.Equal(2, code);
        Assert.Contains("Windows authentication", console.Text);
    }

    [Fact]
    public async Task AddChannel_SavesItsChoices_WithBothUrlsProtected()
    {
        await Run("add-server --server sql01 --name Sales-Prod");

        var (code, console) = await Run("add-channel --name \"DBA team\" --servers Sales-Prod --critical-only --no-ends --heartbeat daily@08:30 --ping --stdin",
                                        new FakeConsole(Webhook, Ping));

        Assert.Equal(0, code);
        var channel = Assert.Single(Saved().ChatChannels);
        Assert.Equal(("DBA team", ChatService.Slack, Webhook, Ping), (channel.Name, channel.Service, channel.WebhookUrl, channel.HeartbeatPingUrl));
        Assert.False(channel.AllConnections);
        Assert.True(channel.CriticalOnly);
        Assert.False(channel.PostEnds);
        Assert.Equal((HeartbeatEvery.Daily, new TimeSpan(8, 30, 0)), (channel.HeartbeatEvery, channel.HeartbeatAt));
        Assert.DoesNotContain("abcdEFGH", FileText());
        Assert.DoesNotContain("hc-ping.com", FileText());
        Assert.DoesNotContain("abcdEFGH", console.Text);
        Assert.Contains("x7Qa", console.Text);   // masked, as the app shows it
    }

    [Theory]
    [InlineData("add-channel --name DBA --stdin", "https://example.com/hook", "Couldn't tell Slack from Teams")]
    [InlineData("add-channel --name DBA --servers nosuch --stdin", Webhook, "no server called nosuch")]
    [InlineData("add-channel --name DBA --ping --stdin", Webhook, "add --heartbeat too")]
    [InlineData("add-channel --name DBA --heartbeat 5h --stdin", Webhook, "--heartbeat must be off")]
    [InlineData("add-channel --stdin", Webhook, "--name is needed")]
    public async Task AddChannel_Refuses_WhatCantBeRight(string line, string url, string says)
    {
        var (code, console) = await Run(line, new FakeConsole(url, Ping));

        Assert.Equal(2, code);
        Assert.Contains(says, console.Text);
        Assert.DoesNotContain("abcdEFGH", console.Text);
    }

    [Fact]
    public async Task Set_TakesOnlyTheAppsChoices()
    {
        Assert.Equal(0, (await Run("set --sample-seconds 30 --retention-days 90 --detail-minutes 15 --alert-samples 5 --command-timeout 60")).Code);
        var settings = Saved();
        Assert.Equal((30, 90, 15, 5, 60), (settings.SampleSeconds, settings.HistoryRetentionDays, settings.HistoryIntervalMinutes,
                                           settings.AlertSamples, settings.CommandTimeoutSeconds));

        var (code, console) = await Run("set --retention-days 45");
        Assert.Equal(2, code);
        Assert.Contains("--retention-days must be one of 7, 14, 30, 90", console.Text);
    }

    [Fact]
    public async Task List_SaysWhatNeedsFixing_WithoutASecret()
    {
        await Run("add-server --server sql01 --sql-login vitals --password-stdin", new FakeConsole("Pa55word"));
        await Run("add-channel --name DBA --stdin", new FakeConsole(Webhook));
        // As if copied from another PC: the password no longer decrypts here.
        File.WriteAllText(CollectorPaths.SettingsFile(_folder.Path), FileText().Replace("dpapi:fake:", "dpapi:gone:", StringComparison.Ordinal));

        var (code, console) = await Run("list");

        Assert.Equal(0, code);
        Assert.Contains("Needs fixing:", console.Text);
        Assert.Contains("set-password \"sql01\"", console.Text);
        Assert.Contains("Chat \"DBA\"", console.Text);
        Assert.DoesNotContain("Pa55word", console.Text);
        Assert.DoesNotContain("abcdEFGH", console.Text);
    }

    [Fact]
    public async Task WhatCantBeDecrypted_SurvivesASaveForSomethingElse_AndTheChannelCanBeRemoved()
    {
        await Run("add-server --server sql01 --sql-login vitals --password-stdin", new FakeConsole("Pa55word"));
        await Run("add-channel --name DBA --stdin", new FakeConsole(Webhook));
        await Run("add-channel --name Ops --stdin", new FakeConsole(Webhook.Replace("x7Qa", "y8Rb", StringComparison.Ordinal)));
        var blobs = FileText().Replace("dpapi:fake:", "dpapi:gone:", StringComparison.Ordinal);
        File.WriteAllText(CollectorPaths.SettingsFile(_folder.Path), blobs);
        var gone = System.Text.RegularExpressions.Regex.Matches(blobs, "dpapi:gone:[^\"]*").Select(m => m.Value).ToList();

        Assert.Equal(0, (await Run("set --sample-seconds 30")).Code);
        Assert.All(gone, blob => Assert.Contains(blob, FileText()));

        Assert.Equal(0, (await Run("remove-channel DBA")).Code);
        var settings = Saved();
        Assert.Equal(["Ops"], settings.UnreadableChannels.Select(c => c.Name));
        Assert.True(settings.Servers[0].PasswordUnreadable);

        Assert.Equal(0, (await Run("set-password sql01", new FakeConsole { Typed = "new" })).Code);
        Assert.Equal("new", Saved().Servers[0].Password);
    }

    [Fact]
    public async Task AFailureNoOneExpected_IsOneLine_WithExitCode1()
    {
        var console = new FakeConsole();
        var code = await new Commands(console, _folder.Path, true, new BrokenProtector(), new FakeWindows())
            .RunAsync(CommandLine.Parse(["add-server", "--server", "sql01", "--sql-login", "vitals", "--password-stdin"]));
        Assert.Equal(2, code);   // no stdin line: said as a usage error

        console = new FakeConsole("Pa55word");
        code = await new Commands(console, _folder.Path, true, new BrokenProtector(), new FakeWindows())
            .RunAsync(CommandLine.Parse(["add-server", "--server", "sql01", "--sql-login", "vitals", "--password-stdin"]));

        Assert.Equal(1, code);
        Assert.Equal("add-server failed: The key isn't there.", console.Error.ToString()!.Trim());
    }

    private sealed class BrokenProtector : ISecretProtector
    {
        public string Protect(string plainText) => throw new System.Security.Cryptography.CryptographicException("The key isn't there.");
        public string Unprotect(string protectedText) => throw new System.Security.Cryptography.CryptographicException("The key isn't there.");
    }

    [Fact]
    public async Task Test_ReadsEachServer_AndPostsToEachChannel()
    {
        await Run("add-server --server sql01");
        await Run("add-channel --name DBA --stdin", new FakeConsole(Webhook));
        var server = Saved().Servers[0];
        var fake   = new FakeServer(() => FakeServer.Snapshot());
        var sender = new FakeSender();
        var console = new FakeConsole();

        var code = await new Commands(console, _folder.Path, true, new FakeProtector(), new FakeWindows(),
                                      (_, _) => new ServerRepositories(fake, fake), () => sender)
            .RunAsync(CommandLine.Parse(["test"]));

        Assert.Equal(0, code);
        Assert.Equal(1, fake.Reads);
        Assert.Contains($"{server.DisplayName}: read it.", console.Text);
        Assert.Contains("Chat \"DBA\": posted the test message.", console.Text);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task AnUnknownCommandOrOption_IsSaid()
    {
        var (code, console) = await Run("watch-everything");
        Assert.Equal(2, code);
        Assert.Contains("no command \"watch-everything\"", console.Text);

        (code, console) = await Run("add-server --server sql01 --databse Sales");
        Assert.Equal(0, code);
        Assert.Contains("Ignored: --databse", console.Text);
    }

    [Theory]
    [InlineData(null, "Off")]
    [InlineData("off", "Off")]
    [InlineData("6h", "Every 6 hours (00:00, 06:00, 12:00, 18:00)")]
    [InlineData("1H", "Every hour")]
    [InlineData("daily@09:00", "Daily at 09:00")]
    public void Heartbeat_ReadsTheAppsSchedules(string? text, string describes) =>
        Assert.Equal(describes, Commands.ParseHeartbeat(text).Describe());

    [Fact]
    public void TheCommandLine_ReadsValuesOptionsAndFlags()
    {
        var line = CommandLine.Parse(["Remove-Server", "Sales Prod", "--data-dir=C:\\x", "--start", "--account", "CONTOSO\\svc$"]);

        Assert.Null(line.Error);
        Assert.Equal("remove-server", line.Command);
        Assert.Equal("Sales Prod", line.Value);
        Assert.Equal("C:\\x", line.Option("data-dir"));
        Assert.True(line.Flag("start"));
        Assert.Equal("CONTOSO\\svc$", line.Option("account"));
        Assert.Equal("--name needs a value.", CommandLine.Parse(["add-channel", "--name"]).Error);
        Assert.Equal("--name is given twice.", CommandLine.Parse(["add-channel", "--name", "a", "--name", "b"]).Error);
    }
}

/// <summary>Windows as install sees it, with every call recorded; admin, and nothing installed, unless told otherwise.</summary>
internal sealed class FakeWindows : IWindowsSetup
{
    public bool IsAdministrator { get; set; } = true;
    public string? ExePath { get; set; } = @"C:\Program Files\SqlVitals Collector\SqlVitals.Collector.exe";
    public bool Installed { get; set; }
    public bool Stops { get; set; } = true;
    public string Sddl { get; set; } = "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCLCSWLOCRRC;;;IU)S:(AU;FA;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;WD)";
    public Func<string[], (int, string)?>? Answer { get; set; }

    public readonly List<string[]> ScCalls = new();
    public readonly List<string> Calls = new();
    public IReadOnlyCollection<SecurityIdentifier>? LockedFor;

    public bool ServiceExists(string name) => Installed;

    public (int ExitCode, string Output) Sc(params string[] args)
    {
        ScCalls.Add(args);
        if (Answer?.Invoke(args) is { } answer)
            return answer;
        return args[0] == "sdshow" ? (0, Sddl + "\r\n") : (0, "[SC] SUCCESS");
    }

    public SecurityIdentifier? Sid(string account) =>
        account.StartsWith("NOBODY", StringComparison.Ordinal) ? null : new SecurityIdentifier("S-1-5-80-1111-2222");

    public WindowsIdentity CurrentUser { get; } = WindowsIdentity.GetCurrent();

    public string? MachineDomain { get; set; } = "CONTOSO";

    public void CreateEventSource(string source) => Calls.Add($"create source {source}");
    public void DeleteEventSource(string source) => Calls.Add($"delete source {source}");

    public void LockFolder(string directory, IReadOnlyCollection<SecurityIdentifier> modify)
    {
        Calls.Add($"lock {directory}");
        LockedFor = modify;
    }

    public void StartService(string name) => Calls.Add($"start {name}");

    public bool StopService(string name, TimeSpan wait)
    {
        Calls.Add($"stop {name}");
        return Stops;
    }
}
