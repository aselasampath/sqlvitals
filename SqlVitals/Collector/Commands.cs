using System.Globalization;
using System.ServiceProcess;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.Service;

namespace SqlVitals.Collector;

/// <summary>
/// The collector's commands (#172): set up its servers and chat channels, test them, install it.
/// Secrets are never taken as a command-line argument, where they'd stay in the shell's history:
/// they're typed at a hidden prompt, or piped in with --password-stdin / --stdin.
/// </summary>
internal sealed class Commands(ICommandConsole console, string dataDirectory, bool customDataDirectory,
                               ISecretProtector protector, IWindowsSetup windows,
                               Func<CollectorServer, CollectorSettings, ServerRepositories>? repositories = null,
                               Func<IWebhookSender>? sender = null)
{
    public const string Usage = """
        SqlVitals Collector: watches SQL Server around the clock as a Windows service (or in this console).

        Usage: SqlVitals.Collector <command> [options]

          run                     Watch the servers in this console until Ctrl+C. (Windows starts the service this way.)
          list                    The servers, chat channels and settings, and anything that needs fixing.
          add-server              --server <name> [--database <db>] [--name <label>] [--sql-login <user>]
                                  [--encrypt mandatory|optional|strict] [--trust-server-certificate]
                                  [--connect-timeout <s>] [--id <guid>]
                                  Windows authentication (the service's account) unless --sql-login; its password
                                  is typed at a hidden prompt, or piped in with --password-stdin.
          set-password <server>   A SQL login's password again. [--password-stdin]
          remove-server <server>
          add-channel             --name <name> [--service slack|teams] [--servers <a,b>] [--critical-only] [--no-ends]
                                  [--heartbeat off|<1-12>h|daily@HH:mm] [--ping]
                                  The webhook URL (then, with --ping, the dead man's switch URL) is typed at a hidden
                                  prompt, or piped in, one per line, with --stdin.
          remove-channel <name>
          set                     [--sample-seconds <5-300>] [--detail-minutes 1|5|15|30] [--retention-days 7|14|30|90]
                                  [--alert-samples 1|2|3|5|10] [--command-timeout <s>]
          test                    Reads each server once and posts a test message to each channel.
          install                 As an administrator: install the Windows service. [--account <name>] [--password-stdin] [--start]
          uninstall               As an administrator: remove it. Its settings and history stay unless --delete-data.

        Every command takes --data-dir <folder>; the default is %ProgramData%\SqlVitals\Collector.
        The service checks its settings every 10 seconds: changes apply without restarting it.
        """;

    private string SettingsPath => CollectorPaths.SettingsFile(dataDirectory);

    /// <summary>Runs one command; the exit code: 0 done, 1 failed, 2 typed wrongly.</summary>
    public async Task<int> RunAsync(CommandLine line)
    {
        try
        {
            if (line.Error is { } error)
                throw new UsageException(error);
            if (line.Flag("help") || line.Command is "help")
            {
                console.Out.WriteLine(Usage);
                return 0;
            }

            var code = line.Command switch
            {
                "list"           => List(),
                "add-server"     => AddServer(line),
                "set-password"   => SetPassword(line),
                "remove-server"  => RemoveServer(line),
                "add-channel"    => AddChannel(line),
                "remove-channel" => RemoveChannel(line),
                "set"            => Set(line),
                "test"           => await TestAsync().ConfigureAwait(false),
                "install"        => Install(line),
                "uninstall"      => new ServiceSetup(windows, console.Out).Uninstall(dataDirectory, line.Flag("delete-data")),
                _                => throw new UsageException($"There's no command \"{line.Command}\". See: SqlVitals.Collector help"),
            };

            if (line.Unused() is { Count: > 0 } unused)
                console.Error.WriteLine($"Ignored: {string.Join(", ", unused.Select(u => "--" + u))} (not an option of {line.Command}).");
            return code;
        }
        catch (UsageException ex)
        {
            console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (CollectorSettingsException ex)
        {
            console.Error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            // sc.exe missing, DPAPI failing, a folder that can't be locked: one line, never a stack trace.
            console.Error.WriteLine($"{line.Command} failed: {Secrets.ForLog(ex.Message)}");
            return 1;
        }
    }

    private CollectorSettingsRead Read() => CollectorSettingsFile.Load(SettingsPath, protector);

    private void Save(CollectorSettings settings)
    {
        DataFolder.EnsureLocked(dataDirectory);
        CollectorSettingsFile.Save(SettingsPath, settings, protector);
    }

    private void Saved(string what)
    {
        console.Out.WriteLine(what);
        console.Out.WriteLine(ServiceState() == ServiceControllerStatus.Running
            ? $"The service applies it within {CollectorHost.SettingsCheckEvery.TotalSeconds:0} seconds."
            : "It applies when the service starts.");
    }

    private int List()
    {
        var (settings, problems) = Read();
        var o = console.Out;
        o.WriteLine($"Settings: {SettingsPath}");
        o.WriteLine($"Service:  {ServiceState()?.ToString() ?? "not installed"}");
        o.WriteLine($"Samples every {settings.SampleSeconds} s · detail every {settings.HistoryIntervalMinutes} min · " +
                    $"history kept {settings.HistoryRetentionDays} days · alerts after {settings.AlertSamples} samples · " +
                    $"query timeout {settings.CommandTimeoutSeconds} s");
        o.WriteLine();
        o.WriteLine(settings.Servers.Count == 0 ? "No servers yet. Add one: SqlVitals.Collector add-server --server <name>" : $"Servers ({settings.Servers.Count}):");
        foreach (var s in settings.Servers)
            o.WriteLine($"  {s.DisplayName} · {s.Summary}{(s.NeedsPassword || s.Unusable is not null ? " · not watched" : "")}   id {s.Id}");
        o.WriteLine();
        o.WriteLine(settings.ChatChannels.Count + settings.UnreadableChannels.Count == 0 ? "No chat channels." : $"Chat channels ({settings.ChatChannels.Count}):");
        foreach (var c in settings.ChatChannels)
            o.WriteLine($"  {c} · {Servers(c, settings)} · {(c.CriticalOnly ? "critical only" : "warnings and critical")} · " +
                        $"heartbeat {c.Heartbeat.Describe().ToLowerInvariant()}{(string.IsNullOrEmpty(c.HeartbeatPingUrl) ? "" : " with a ping")}" +
                        (c.Enabled ? "" : " · off"));
        if (problems.Count > 0)
        {
            o.WriteLine();
            o.WriteLine("Needs fixing:");
            foreach (var p in problems)
                o.WriteLine($"  {p}");
        }
        return 0;
    }

    private static string Servers(ChatChannel channel, CollectorSettings settings) =>
        channel.AllConnections
            ? "all servers"
            : ChatMessageBuilder.List(channel.ConnectionIds.Select(id => settings.Servers.FirstOrDefault(s => s.Id == id)?.DisplayName)
                                                           .OfType<string>().ToList()) is { Length: > 0 } names ? names : "no servers";

    private int AddServer(CommandLine line)
    {
        var settings = Read().Settings;
        var server = new CollectorServer
        {
            Server                 = Required(line, "server"),
            Database               = line.Option("database")?.Trim() ?? string.Empty,
            Name                   = line.Option("name")?.Trim() ?? string.Empty,
            TrustServerCertificate = line.Flag("trust-server-certificate"),
            Encrypt                = ParseEnum(line, "encrypt", CollectorEncrypt.Mandatory),
            ConnectTimeoutSeconds  = line.Number("connect-timeout") is { } seconds ? Math.Clamp(seconds, 1, 600) : 30,
        };
        if (line.Option("id") is { } id)
            server.Id = Guid.TryParse(id, out var guid) && guid != Guid.Empty
                ? guid
                : throw new UsageException($"--id must be a GUID, not \"{id}\".");
        if (settings.Servers.Any(s => s.Id == server.Id))
            throw new UsageException($"A server with the id {server.Id} is already there.");
        if (settings.Servers.FirstOrDefault(s => SameServer(s, server)) is { } existing)
            throw new UsageException($"{existing.DisplayName} already watches {server.Summary}. Remove it first, or give another --database.");
        if (settings.Servers.Any(s => string.Equals(s.DisplayName, server.DisplayName, StringComparison.OrdinalIgnoreCase)))
            throw new UsageException($"There's already a server called {server.DisplayName}: give this one another --name.");

        if (line.Option("sql-login") is { } login)
        {
            server.Authentication = CollectorAuth.SqlLogin;
            server.UserName       = login.Trim();
            server.Password       = Secret(line.Flag("password-stdin"), $"Password for {server.UserName}: ", "password", "password-stdin");
        }

        settings.Servers.Add(server);
        Save(settings);
        Saved($"Added {server.DisplayName} ({server.Summary}).");
        return 0;
    }

    private static bool SameServer(CollectorServer a, CollectorServer b) =>
        string.Equals(a.Server.Trim(), b.Server.Trim(), StringComparison.OrdinalIgnoreCase) &&
        string.Equals(a.Database.Trim(), b.Database.Trim(), StringComparison.OrdinalIgnoreCase);

    private int SetPassword(CommandLine line)
    {
        var settings = Read().Settings;
        var server = FindServer(settings, line);
        if (server.Authentication != CollectorAuth.SqlLogin)
            throw new UsageException($"{server.DisplayName} signs in with Windows authentication: it has no password.");
        server.Password = Secret(line.Flag("password-stdin"), $"Password for {server.UserName}: ", "password", "password-stdin");
        Save(settings);
        Saved($"Saved the password of {server.DisplayName}.");
        return 0;
    }

    private int RemoveServer(CommandLine line)
    {
        var settings = Read().Settings;
        var server = FindServer(settings, line);
        settings.Servers.Remove(server);
        foreach (var channel in settings.ChatChannels)
            channel.ConnectionIds.Remove(server.Id);
        Save(settings);
        Saved($"Removed {server.DisplayName}. Its history is kept for {settings.HistoryRetentionDays} days.");
        return 0;
    }

    private int AddChannel(CommandLine line)
    {
        var settings = Read().Settings;
        var channel = new ChatChannel
        {
            Name         = Required(line, "name"),
            CriticalOnly = line.Flag("critical-only"),
            PostEnds     = !line.Flag("no-ends"),
        };
        if (settings.FindChannel(channel.Name) is not null)
            throw new UsageException($"There's already a chat channel called {channel.Name}.");

        if (line.Option("servers") is { } names)
        {
            channel.AllConnections = false;
            foreach (var name in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                channel.ConnectionIds.Add(settings.FindServer(name)?.Id
                                          ?? throw new UsageException($"There's no server called {name}. See: SqlVitals.Collector list"));
        }

        var heartbeat = ParseHeartbeat(line.Option("heartbeat"));
        channel.HeartbeatEvery = heartbeat.Every;
        channel.HeartbeatHours = heartbeat.Every == HeartbeatEvery.Hours ? heartbeat.Hours : channel.HeartbeatHours;
        channel.HeartbeatAt    = heartbeat.At;

        var stdin = line.Flag("stdin");
        channel.WebhookUrl = Secret(stdin, "Webhook URL: ", "webhook URL", "stdin").Trim();
        var service = line.Option("service") is { } named
            ? ParseEnumText<ChatService>(named, "service")
            : WebhookUrl.Detect(channel.WebhookUrl) ?? throw new UsageException("Couldn't tell Slack from Teams by the URL: add --service slack or --service teams.");
        channel.Service = service;
        var check = WebhookUrl.Check(channel.WebhookUrl, service, settings.ChatChannels, channel.Id);
        if (check.Error is { } urlError)
            throw new UsageException(urlError);
        if (check.Warning is { } warning)
            console.Error.WriteLine(warning);

        if (line.Flag("ping"))
        {
            if (!heartbeat.IsOn)
                throw new UsageException("A ping is called with each heartbeat: add --heartbeat too.");
            channel.HeartbeatPingUrl = Secret(stdin, "Ping URL: ", "ping URL", "stdin").Trim();
            if (HeartbeatPing.Check(channel.HeartbeatPingUrl, channel.WebhookUrl) is { } pingError)
                throw new UsageException(pingError);
        }

        settings.ChatChannels.Add(channel);
        Save(settings);
        Saved($"Added {channel}. Check it with: SqlVitals.Collector test");
        if (heartbeat.IsOn)
        {
            console.Out.WriteLine();
            console.Out.WriteLine(HeartbeatSetup.Steps(service, heartbeat, Environment.MachineName));
        }
        return 0;
    }

    private int RemoveChannel(CommandLine line)
    {
        var settings = Read().Settings;
        var name = line.Value ?? throw new UsageException("Which channel? SqlVitals.Collector remove-channel <name>");
        var channel = settings.FindChannel(name) ?? throw new UsageException($"There's no chat channel called {name}. See: SqlVitals.Collector list");
        settings.RemoveChannel(channel);
        Save(settings);
        Saved($"Removed the chat channel {channel.Name}.");
        return 0;
    }

    private int Set(CommandLine line)
    {
        var settings = Read().Settings;
        var changed = new List<string>();

        if (line.Number("sample-seconds") is { } sample)
        {
            settings.SampleSeconds = CollectorSettings.NormalizeSampleSeconds(sample) == sample
                ? sample : throw new UsageException("--sample-seconds must be 5 to 300.");
            changed.Add($"samples every {sample} s");
        }
        if (line.Number("detail-minutes") is { } detail)
        {
            settings.HistoryIntervalMinutes = OneOf(detail, Engine.History.HistorySettings.IntervalChoicesMinutes, "detail-minutes");
            changed.Add($"detail every {detail} min");
        }
        if (line.Number("retention-days") is { } days)
        {
            settings.HistoryRetentionDays = OneOf(days, Engine.History.HistorySettings.RetentionChoicesDays, "retention-days");
            changed.Add($"history kept {days} days");
        }
        if (line.Number("alert-samples") is { } samples)
        {
            settings.AlertSamples = OneOf(samples, Engine.Alerts.AlertSettings.SampleChoices, "alert-samples");
            changed.Add($"alerts after {samples} samples");
        }
        if (line.Number("command-timeout") is { } timeout)
        {
            settings.CommandTimeoutSeconds = timeout is >= 5 and <= 600 ? timeout : throw new UsageException("--command-timeout must be 5 to 600.");
            changed.Add($"query timeout {timeout} s");
        }
        if (changed.Count == 0)
            throw new UsageException("Nothing to set. See: SqlVitals.Collector help");

        Save(settings);
        Saved($"Set {string.Join(", ", changed)}.");
        return 0;
    }

    private async Task<int> TestAsync()
    {
        var (settings, problems) = Read();
        foreach (var problem in problems)
            console.Error.WriteLine(problem);
        var failed = problems.Count > 0;

        foreach (var server in settings.Servers)
        {
            if (server.NeedsPassword)
            {
                console.Out.WriteLine($"{server.DisplayName}: no password, not read.");
                failed = true;
                continue;
            }
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(settings.CommandTimeoutSeconds, server.ConnectTimeoutSeconds) + 15));
                var read = (repositories ?? CollectorHost.RealRepositories)(server, settings);
                await read.Live.GetLiveMetricsAsync(timeout.Token).ConfigureAwait(false);
                console.Out.WriteLine($"{server.DisplayName}: read it.");
            }
            catch (Exception ex)
            {
                var detail = ex is WaitStatsException { InnerException: { } inner } ? inner.Message : ex.Message;
                console.Out.WriteLine($"{server.DisplayName}: couldn't read it: {Secrets.ForLog(detail)}");
                failed = true;
            }
        }
        if (settings.Servers.Count > 0)
            console.Out.WriteLine("(This command reads as you. The service reads as its own account, which needs its own login on each server.)");

        if (settings.ChatChannels.Count > 0)
        {
            using var chat = new ChatNotificationService((sender ?? (() => new HttpWebhookSender()))(), CollectorHost.PcName);
            chat.Apply(settings.ChatChannels, settings.Servers.ToDictionary(s => s.Id, s => s.DisplayName));
            foreach (var channel in settings.ChatChannels)
            {
                var result = await chat.SendTestAsync(channel.Id).ConfigureAwait(false);
                console.Out.WriteLine($"Chat \"{channel.Name}\": {(result.IsSuccess ? "posted the test message." : result.LogText())}");
                failed |= !result.IsSuccess;
            }
        }
        return failed ? 1 : 0;
    }

    private int Install(CommandLine line)
    {
        var account  = line.Option("account");
        var password = line.Flag("password-stdin") ? console.ReadLine() : null;
        return new ServiceSetup(windows, console.Out).Install(dataDirectory, customDataDirectory, account, password, line.Flag("start"));
    }

    private ServiceControllerStatus? ServiceState()
    {
        if (!windows.ServiceExists(CollectorPaths.ServiceName))
            return null;
        try
        {
            using var service = new ServiceController(CollectorPaths.ServiceName);
            return service.Status;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static CollectorServer FindServer(CollectorSettings settings, CommandLine line)
    {
        var name = line.Value ?? throw new UsageException("Which server? Give its name, server name or id.");
        return settings.FindServer(name) ?? throw new UsageException($"There's no server called {name}. See: SqlVitals.Collector list");
    }

    private static string Required(CommandLine line, string option) =>
        line.Option(option) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value.Trim()
            : throw new UsageException($"--{option} is needed. See: SqlVitals.Collector help");

    // A secret from standard input or a hidden prompt; never from the command line itself.
    private string Secret(bool fromStdin, string prompt, string what, string stdinOption)
    {
        var secret = fromStdin ? console.ReadLine() : console.ReadSecret(prompt);
        if (secret is null && !fromStdin)
            throw new UsageException($"There's no console to type the {what} at: pipe it in with --{stdinOption}.");
        return string.IsNullOrEmpty(secret) ? throw new UsageException($"No {what} was given.") : secret;
    }

    private static T ParseEnum<T>(CommandLine line, string option, T fallback) where T : struct, Enum =>
        line.Option(option) is { } text ? ParseEnumText<T>(text, option) : fallback;

    private static T ParseEnumText<T>(string text, string option) where T : struct, Enum =>
        Enum.TryParse<T>(text, ignoreCase: true, out var value) && Enum.IsDefined(value)
            ? value
            : throw new UsageException($"--{option} must be one of {string.Join(", ", Enum.GetNames<T>().Select(n => n.ToLowerInvariant()))}.");

    private static int OneOf(int value, IReadOnlyList<int> choices, string option) =>
        choices.Contains(value) ? value : throw new UsageException($"--{option} must be one of {string.Join(", ", choices)}.");

    /// <summary>"off", "6h", "daily@09:00".</summary>
    internal static HeartbeatSchedule ParseHeartbeat(string? text)
    {
        var value = text?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value) || value == "off")
            return HeartbeatSchedule.Off;
        if (value.EndsWith('h') && int.TryParse(value[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var hours) &&
            HeartbeatSchedule.AllowedHours.Contains(hours))
            return HeartbeatSchedule.EveryHours(hours);
        if (value.StartsWith("daily@", StringComparison.Ordinal) &&
            TimeSpan.TryParseExact(value[6..], @"hh\:mm", CultureInfo.InvariantCulture, out var at))
            return HeartbeatSchedule.DailyAt(at);
        throw new UsageException($"--heartbeat must be off, one of {string.Join(", ", HeartbeatSchedule.AllowedHours.Select(h => h + "h"))}, or daily@HH:mm.");
    }
}
