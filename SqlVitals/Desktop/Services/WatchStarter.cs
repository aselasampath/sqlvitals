using Microsoft.Extensions.Logging;

namespace SqlVitals.Desktop.Services;

/// <summary>What the "where to watch it" question was answered (#172).</summary>
/// <param name="Remember">Don't ask again was ticked: the choice is saved and asked no more.</param>
public sealed record WatchAnswer(WatchChoice Choice, bool Remember);

/// <summary>What pressing Start does (#172): ask where to watch the server when the collector service is installed.</summary>
public interface IWatchStarter
{
    /// <summary>
    /// Starts watching a connection: in SqlVitals, or by handing it to the collector service, as
    /// the user chooses (or chose with Don't ask again). Without the service, the same as
    /// <see cref="MonitoringManager.Start"/>. Never throws: a failure is said, and the server is
    /// then watched in SqlVitals.
    /// </summary>
    /// <param name="source">Where Start was pressed, for the activity log.</param>
    [ReportsItsErrors]
    Task StartAsync(Guid connectionId, string source);
}

/// <summary>Start without a service to ask about: what pages fall back to when built without one (tests).</summary>
public sealed class DirectWatchStarter(MonitoringManager monitoring) : IWatchStarter
{
    [ReportsItsErrors]
    public Task StartAsync(Guid connectionId, string source)
    {
        monitoring.Start(connectionId, source);
        return Task.CompletedTask;
    }
}

/// <summary>
/// The app's <see cref="IWatchStarter"/> (#172). With the collector service installed and its
/// settings this user's to change, Start asks: watch it with the service, which keeps going when
/// SqlVitals is closed, or in SqlVitals, as before. A server the service already has needs no
/// question: Start offers to start the service when it's stopped.
/// </summary>
public sealed class WatchStarter(MonitoringManager monitoring, CollectorLink collectors, ConnectionSettingsService settings,
                                 IDialogService dialogs, ILogger<WatchStarter>? log = null) : IWatchStarter
{
    private const string Caption = "Start monitoring";

    private readonly ILogger _log = log ?? AppLog.For<WatchStarter>();

    [ReportsItsErrors]
    public async Task StartAsync(Guid connectionId, string source)
    {
        try
        {
            await StartOrHandOverAsync(connectionId, source);
        }
        catch (Exception ex)
        {
            _log.Error("Monitoring: couldn't start watching a server", ex);
            dialogs.Warn($"Couldn't start watching it: {ConnectionSettingsService.RedactSecrets(ex.Message)}", Caption);
        }
    }

    private async Task StartOrHandOverAsync(Guid connectionId, string source)
    {
        var view = collectors.View;
        var store = settings.Load();
        if (store.Connections.FirstOrDefault(c => c.Id == connectionId) is not { } connection)
            return;

        // A collector has it already.
        if (monitoring.WatchedElsewhereReason(connectionId) is { } reason)
        {
            if (view.HandedOver.Contains(connectionId) && view.State == CollectorServiceState.Stopped)
            {
                if (dialogs.Confirm($"{connection.DisplayName} is handed to {CollectorLink.ServiceName}, which is stopped, so nothing watches it. Start the service?",
                                    Caption))
                    await collectors.StartServiceAsync();
            }
            else
            {
                dialogs.Inform(reason, Caption);
            }
            return;
        }

        // No service, or not this user's to change, or a server it can't take: as without it.
        if (!view.IsInstalled || !view.CanEdit || CollectorHandover.WhyNot(connection) is not null)
        {
            monitoring.Start(connectionId, source);
            return;
        }

        var choice = store.WatchChoice;
        if (choice == WatchChoice.Ask)
        {
            if (dialogs.AskWhereToWatch(connection.DisplayName, view.State == CollectorServiceState.Running) is not { } answer)
                return;
            choice = answer.Choice;
            if (answer.Remember)
                settings.SetWatchChoice(choice);
        }

        if (choice == WatchChoice.App)
        {
            monitoring.Start(connectionId, source);
            return;
        }

        try
        {
            await collectors.HandOverAsync(connectionId);
        }
        catch (Exception ex)
        {
            // HandOverAsync took it back from a service that wouldn't start: the app has it again.
            _log.Error("Monitoring: couldn't hand a server to the collector service", ex);
            dialogs.Warn($"Couldn't hand {connection.DisplayName} to the collector service, so SqlVitals watches it instead: " +
                         ConnectionSettingsService.RedactSecrets(ex.Message), Caption);
            monitoring.Start(connectionId, source);
        }
    }
}
