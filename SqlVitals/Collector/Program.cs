using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Service;

namespace SqlVitals.Collector;

/// <summary>
/// SqlVitals.Collector.exe (#172): with no command, or <c>run</c>, it watches the servers in
/// collector.json until stopped, as the Windows service when Windows starts it, in the console
/// otherwise; any other command sets it up (see <see cref="Commands"/>).
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var line = CommandLine.Parse(args);
        var dataDirectory = line.Option("data-dir") is { Length: > 0 } dir ? Path.GetFullPath(dir) : CollectorPaths.DataDirectory;

        if (WindowsServiceHelpers.IsWindowsService() || line.Command is null or "run")
        {
            if (line.Error is { } error)
            {
                await Console.Error.WriteLineAsync(error);
                return 2;
            }
            return await RunAsync(dataDirectory);
        }

        // The lists use "·" and the messages "…".
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        return await new Commands(new SystemConsole(), dataDirectory, line.Option("data-dir") is not null,
                                  new MachineSecretProtector(), new WindowsSetup()).RunAsync(line);
    }

    // The service, or the console until Ctrl+C.
    private static async Task<int> RunAsync(string dataDirectory)
    {
        var asService = WindowsServiceHelpers.IsWindowsService();
        // A first try in the console mustn't leave the folder readable by every user: it holds
        // the secrets. The service's account can't change permissions; install has set them.
        if (!asService)
            DataFolder.EnsureLocked(dataDirectory);
        var fileLog   = new CollectorFileLoggerProvider(CollectorPaths.LogDirectory(dataDirectory));
        fileLog.DeleteExpiredFiles(DateTime.Today);

        // No args: the host's own command-line configuration would read ours.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = CollectorPaths.DisplayName,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(fileLog);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Services.AddWindowsService(options => options.ServiceName = CollectorPaths.ServiceName);
        if (asService)
        {
            // Install creates the source; the Event Log gets what an admin should look at.
            builder.Logging.AddEventLog(settings => settings.SourceName = CollectorPaths.DisplayName);
            builder.Logging.AddFilter<EventLogLoggerProvider>(level => level >= LogLevel.Warning);
        }
        else
        {
            builder.Logging.AddSimpleConsole(options =>
            {
                options.SingleLine      = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
        }
        // Stopping tells the channels and flushes the history: well under Windows' 20 seconds.
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(20));
        builder.Services.AddSingleton(s => new CollectorHost(
            dataDirectory, new MachineSecretProtector(), CollectorHost.RealRepositories, new HttpWebhookSender(),
            s.GetRequiredService<ILoggerFactory>().CreateLogger("SqlVitals.Collector")));
        builder.Services.AddHostedService<CollectorWorker>();

        try
        {
            using var host = builder.Build();
            await host.RunAsync();
            return 0;
        }
        catch (Exception ex)
        {
            // The settings, the history's folder, or anything else: said in the log, the Event Log
            // (through the host's logger, before this) and the console, never a bare crash.
            var text = $"SqlVitals Collector couldn't start: {Engine.Errors.Secrets.ForLog(ex.Message)}";
            fileLog.Write(LogLevel.Critical, ex is CollectorSettingsException ? text : $"{text}{Environment.NewLine}{ex}");
            await Console.Error.WriteLineAsync(text);
            return 1;
        }
    }
}

/// <summary>Starts and stops the <see cref="CollectorHost"/> with the service (or the console).</summary>
internal sealed class CollectorWorker(CollectorHost host) : IHostedService, IDisposable
{
    public Task StartAsync(CancellationToken cancellationToken) => host.StartAsync();

    public Task StopAsync(CancellationToken cancellationToken) => host.StopAsync();

    public void Dispose() => host.Dispose();
}
