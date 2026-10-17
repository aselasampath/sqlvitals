using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.ViewModels.Settings;

/// <summary>
/// Settings → Collector service (#172): the optional service on this PC (start, stop), what Start
/// does while it's installed, each server's hand over and take back, and a collector on another
/// PC whose share copy is read. Without the service it says how to get it; nothing else changes.
/// </summary>
public sealed partial class CollectorCategoryViewModel : SettingsCategoryViewModel
{
    private readonly SettingsContext _context;
    private readonly CollectorLink?  _collectors;
    private WatchChoice _watchChoice;

    internal CollectorCategoryViewModel(SettingsContext context)
    {
        _context    = context;
        _collectors = context.Collectors;
        var store = context.Service.Load();
        _watchChoice = store.WatchChoice;
        RemotePath   = store.CollectorHistoryPath;
        Render();
    }

    public override string Subtitle => SettingsNav.Info(SettingsCategory.Collector).Subtitle;

    public override void OnShownAgain() => Render();

    // Settings keeps a category it built: it listens while it's shown, again each time.
    protected override void OnActivated()
    {
        if (_collectors is null)
            return;
        _collectors.Changed += Render;
        Render();
    }

    protected override void OnDeactivated() => _collectors?.Changed -= Render;

    // ── The service on this PC ────────────────────────────────────────────────

    /// <summary>"Running. It watches 2 of your servers…", "Not installed…".</summary>
    [ObservableProperty]
    private string _stateText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartServiceCommand))]
    private bool _canStart;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopServiceCommand))]
    private bool _canStop;

    public StatusLine ServiceStatus { get; } = new();

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartService() => RunAsync(link => link.StartServiceAsync(), ServiceStatus, "Started the service.");

    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task StopService() => RunAsync(link => link.StopServiceAsync(), ServiceStatus,
                                           "Stopped the service: the servers handed to it aren't watched until it starts again.");

    // ── What Start does ───────────────────────────────────────────────────────

    /// <summary>In <see cref="WatchChoice"/>'s order.</summary>
    public IReadOnlyList<Choice<WatchChoice>> WatchChoices { get; } =
    [
        new(WatchChoice.Ask, "Ask each time"),
        new(WatchChoice.App, "Watch in SqlVitals"),
        new(WatchChoice.Service, "Hand to the collector service"),
    ];

    /// <summary>What ▶ Start does for a server while the service is installed: saved as it's picked.</summary>
    public WatchChoice WatchChoice
    {
        get => _watchChoice;
        set
        {
            if (value == _watchChoice)
                return;
            try
            {
                _context.Service.SetWatchChoice(value);
                _watchChoice = value;
                OnPropertyChanged();
                ChoiceStatus.Show(value switch
                {
                    WatchChoice.App     => "Saved: ▶ Start watches the server in SqlVitals.",
                    WatchChoice.Service => "Saved: ▶ Start hands the server to the collector service.",
                    _                   => "Saved: ▶ Start asks where to watch the server.",
                }, success: true);
            }
            catch (Exception ex)
            {
                ChoiceStatus.Show($"Could not save the setting: {ex.Message}", success: false);
                _context.Ui.Post(() => OnPropertyChanged(nameof(WatchChoice)));
            }
        }
    }

    public StatusLine ChoiceStatus { get; } = new();

    // ── Your servers ──────────────────────────────────────────────────────────

    /// <summary>Each server, by name: who watches it, and the button that moves it.</summary>
    [ObservableProperty]
    private IReadOnlyList<CollectorServerRow> _rows = [];

    public StatusLine ServersStatus { get; } = new();

    // ── A collector on another PC ─────────────────────────────────────────────

    /// <summary>The folder or file of another PC's share copy, as typed; saved with Save.</summary>
    [ObservableProperty]
    private string _remotePath = string.Empty;

    public StatusLine RemoteStatus { get; } = new();

    [RelayCommand]
    private void SaveRemote()
    {
        try
        {
            _context.Service.SetCollectorHistoryPath(RemotePath);
            RemoteStatus.Show(RemotePath.Trim().Length == 0
                ? "Saved: no collector on another PC is read."
                : "Saved: reading it within a few seconds.", success: true);
            if (_collectors is not null)
                _ = RunAsync(link => link.RefreshAsync(), RemoteStatus, "Read it.");
        }
        catch (Exception ex)
        {
            RemoteStatus.Show($"Could not save the path: {ex.Message}", success: false);
        }
    }

    // ── Showing it ────────────────────────────────────────────────────────────

    private void Render()
    {
        var view  = _collectors?.View ?? CollectorView.None;
        var store = _context.Service.Load();

        StateText = view.State switch
        {
            CollectorServiceState.NotInstalled =>
                "Not installed. SqlVitals watches your servers itself while it runs. To keep watching when it's closed, install SqlVitals.Collector " +
                "from the release's SqlVitals-Collector zip on this or an always-on PC (see Collector Service in the docs).",
            CollectorServiceState.Running  => $"Running. It watches {Count(view.HandedOver.Count)} of your servers, and keeps going when SqlVitals is closed.",
            CollectorServiceState.Stopped  => $"Stopped. {Count(view.HandedOver.Count)} of your servers are handed to it, and aren't watched until it starts.",
            var state                      => $"{state}…",
        } + (view.Problem is { } problem ? $" {problem}" : "");
        CanStart = view.State == CollectorServiceState.Stopped;
        CanStop  = view.State == CollectorServiceState.Running;

        Rows = store.Connections
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => Row(c, view))
            .ToList();

        if (view.RemoteProblem is { } remote)
            RemoteStatus.Show(remote, success: false);
        else if (view.RemotePath is { } path)
            RemoteStatus.Show(
                $"Reading it: {Count(view.Watches.Count(w => w.Value.By == $"the collector at {path}"))} of your servers matched by server and database.",
                success: null);
    }

    private static string Count(int n) => n.ToString(CultureInfo.CurrentCulture);

    private CollectorServerRow Row(ConnectionSettings connection, CollectorView view)
    {
        var id     = connection.Id;
        var handed = view.HandedOver.Contains(id);
        if (handed)
            return new CollectorServerRow(connection.DisplayName,
                view.Watches.TryGetValue(id, out var watch) && watch.NotWatching is { } why ? why : $"Watched by {CollectorLink.ServiceName}.",
                "Take back", $"Take {connection.DisplayName} back from the service", "SqlVitals watches it again, as its settings say.",
                new AsyncRelayCommand(() => RunAsync(link => link.TakeBackAsync(id), ServersStatus,
                                                     $"Took {connection.DisplayName} back: SqlVitals watches it as its settings say.")),
                view.CanEdit);

        if (view.Watches.TryGetValue(id, out var remote))
            return new CollectorServerRow(connection.DisplayName, $"Watched by {remote.By}.", "Hand over", $"Hand {connection.DisplayName} to the service",
                "Another PC's collector watches it.", new RelayCommand(() => { }), false);

        var whyNot = !view.IsInstalled ? "The collector service isn't installed on this PC."
                   : !view.CanEdit     ? view.Problem ?? "The service's settings can't be changed by this Windows user."
                   : CollectorHandover.WhyNot(connection);
        return new CollectorServerRow(connection.DisplayName,
            _context.Monitoring.IsMonitoring(id) ? "Watched in SqlVitals." : "Watched in SqlVitals when it's started.",
            "Hand over", $"Hand {connection.DisplayName} to the service",
            whyNot ?? "The service watches it from now on, and keeps going when SqlVitals is closed.",
            new AsyncRelayCommand(() => RunAsync(link => link.HandOverAsync(id), ServersStatus,
                                                 $"Handed {connection.DisplayName} to the service. Its Windows account needs a login on the server: see Collector Service in the docs.")),
            whyNot is null);
    }

    // A change to the service: said in the card's status line, never thrown.
    [ReportsItsErrors]
    private async Task RunAsync(Func<CollectorLink, Task> change, StatusLine status, string done)
    {
        if (_collectors is null)
            return;
        status.Show("Working…", success: null);
        try
        {
            await change(_collectors);
            status.Show(done, success: true);
        }
        catch (Exception ex)
        {
            status.Show($"Could not: {ex.Message}", success: false);
        }
        Render();
    }
}
