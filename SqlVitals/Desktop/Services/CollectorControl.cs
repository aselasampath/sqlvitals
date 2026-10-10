using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;
using SqlVitals.Engine.Service;

namespace SqlVitals.Desktop.Services;

/// <summary>The collector service as the app sees it (#172).</summary>
public enum CollectorServiceState
{
    /// <summary>Not on this PC: the app watches every server itself, as before.</summary>
    NotInstalled,
    Stopped,
    Starting,
    Running,
    Stopping,
}

/// <summary>
/// The SqlVitals Collector service on this PC and its settings file (#172), behind an interface so
/// tests need no service. Install grants the installing user start and stop, and Modify on the
/// data folder; another Windows user gets <see cref="UnauthorizedAccessException"/> from the file.
/// </summary>
public interface ICollectorControl
{
    /// <summary>Its data folder: collector.json and history.db.</summary>
    string DataDirectory { get; }

    CollectorServiceState State { get; }

    /// <summary>Starts it and waits until it runs. Asks Windows for an admin when this user may not.</summary>
    Task StartAsync(CancellationToken cancel = default);

    /// <summary>Stops it and waits until it has. Asks Windows for an admin when this user may not.</summary>
    Task StopAsync(CancellationToken cancel = default);

    /// <summary>Reads collector.json; throws <see cref="UnauthorizedAccessException"/> for a user it isn't shared with.</summary>
    CollectorSettingsRead Read();

    /// <summary>Saves collector.json; the service applies it within <c>CollectorHost.SettingsCheckEvery</c>.</summary>
    void Save(CollectorSettings settings);
}

/// <summary>The real service, through ServiceController, and its file with DPAPI LocalMachine.</summary>
public sealed class WindowsCollectorControl : ICollectorControl
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);
    private readonly MachineSecretProtector _protector = new();

    public string DataDirectory => CollectorPaths.DataDirectory;

    public CollectorServiceState State
    {
        get
        {
            try
            {
                using var service = new ServiceController(CollectorPaths.ServiceName);
                return service.Status switch
                {
                    ServiceControllerStatus.Running                                          => CollectorServiceState.Running,
                    ServiceControllerStatus.StartPending or ServiceControllerStatus.ContinuePending => CollectorServiceState.Starting,
                    ServiceControllerStatus.StopPending or ServiceControllerStatus.PausePending     => CollectorServiceState.Stopping,
                    _                                                                        => CollectorServiceState.Stopped,
                };
            }
            catch (InvalidOperationException)
            {
                // "Service SqlVitalsCollector was not found on computer '.'".
                return CollectorServiceState.NotInstalled;
            }
        }
    }

    public Task StartAsync(CancellationToken cancel = default) =>
        Change("start", ServiceControllerStatus.Running, service => service.Start(), cancel);

    public Task StopAsync(CancellationToken cancel = default) =>
        Change("stop", ServiceControllerStatus.Stopped, service => service.Stop(), cancel);

    // On the thread pool: waiting for the service mustn't hold up the window.
    private static Task Change(string verb, ServiceControllerStatus target, Action<ServiceController> change, CancellationToken cancel) =>
        Task.Run(() =>
        {
            using var service = new ServiceController(CollectorPaths.ServiceName);
            if (service.Status == target)
                return;
            try
            {
                change(service);
            }
            catch (InvalidOperationException ex) when (ex.InnerException is Win32Exception { NativeErrorCode: 5 })
            {
                // Installed by another user, so this one may not: Windows asks for an admin.
                Elevated(verb);
            }
            service.WaitForStatus(target, Wait);
            cancel.ThrowIfCancellationRequested();
        }, cancel);

    private static void Elevated(string verb)
    {
        try
        {
            using var sc = Process.Start(new ProcessStartInfo("sc.exe", $"{verb} {CollectorPaths.ServiceName}")
            {
                UseShellExecute = true,
                Verb            = "runas",
                WindowStyle     = ProcessWindowStyle.Hidden,
            });
            sc?.WaitForExit();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new OperationCanceledException("The administrator prompt was cancelled.", ex);
        }
    }

    public CollectorSettingsRead Read() => CollectorSettingsFile.Load(CollectorPaths.SettingsFile(DataDirectory), _protector);

    public void Save(CollectorSettings settings)
    {
        if (!Directory.Exists(DataDirectory))
            throw new DirectoryNotFoundException($"{DataDirectory} isn't there: install the collector service first.");
        CollectorSettingsFile.Save(CollectorPaths.SettingsFile(DataDirectory), settings, _protector);
    }
}
