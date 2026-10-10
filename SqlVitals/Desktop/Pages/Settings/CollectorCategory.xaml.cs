using System.Windows.Controls;
using System.Windows.Input;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>A server in Settings → Collector service: who watches it, and the button that moves it (#172).</summary>
internal sealed record CollectorServerRow(string Name, string Where, string ActionText, string ActionName, string? ActionToolTip,
                                          ICommand Act, bool CanAct);

/// <summary>
/// Settings → Collector service (#172): the optional service on this PC (start, stop), what Start
/// does while it's installed, each server's hand over and take back, and a collector on another
/// PC whose share copy is read. Without the service it says how to get it; nothing else changes.
/// </summary>
internal partial class CollectorCategory : UserControl, ISettingsCategory
{
    private readonly SettingsContext _context;
    private readonly CollectorLink?  _collectors;
    private readonly bool _loading;

    public CollectorCategory(SettingsContext context)
    {
        _context    = context;
        _collectors = context.Collectors;
        StartService = new RelayCommand(() => _ = RunAsync(link => link.StartServiceAsync(), TxtServiceStatus, "Started the service."));
        StopService  = new RelayCommand(() => _ = RunAsync(link => link.StopServiceAsync(), TxtServiceStatus,
                                                           "Stopped the service: the servers handed to it aren't watched until it starts again."));
        SaveRemote   = new RelayCommand(SaveRemotePath);
        InitializeComponent();
        TxtSubtitle.Text = SettingsNav.Info(SettingsCategory.Collector).Subtitle;

        _loading = true;
        // In WatchChoice's order.
        CmbChoice.ItemsSource = new[] { "Ask each time", "Watch in SqlVitals", "Hand to the collector service" };
        var store = _context.Service.Load();
        CmbChoice.SelectedIndex = (int)store.WatchChoice;
        TxtRemotePath.Text      = store.CollectorHistoryPath;
        _loading = false;

        if (_collectors is not null)
        {
            // Settings keeps a category it built: it listens while it's shown, again each time.
            _collectors.Changed += Render;
            Loaded   += (_, _) => { _collectors.Changed -= Render; _collectors.Changed += Render; };
            Unloaded += (_, _) => _collectors.Changed -= Render;
        }
        Render();
    }

    public ICommand StartService { get; }
    public ICommand StopService  { get; }
    public ICommand SaveRemote   { get; }

    public Task<bool> TryLeaveAsync() => Task.FromResult(true);

    public void OnShownAgain() => Render();

    /// <summary>The rows shown, for tests.</summary>
    internal IReadOnlyList<CollectorServerRow> Rows { get; private set; } = [];

    private void Render()
    {
        var view  = _collectors?.View ?? CollectorView.None;
        var store = _context.Service.Load();

        TxtState.Text = view.State switch
        {
            CollectorServiceState.NotInstalled =>
                "Not installed. SqlVitals watches your servers itself while it runs. To keep watching when it's closed, install SqlVitals.Collector " +
                "from the release's SqlVitals-Collector zip on this or an always-on PC (see Collector Service in the docs).",
            CollectorServiceState.Running  => $"Running. It watches {Count(view.HandedOver.Count)} of your servers, and keeps going when SqlVitals is closed.",
            CollectorServiceState.Stopped  => $"Stopped. {Count(view.HandedOver.Count)} of your servers are handed to it, and aren't watched until it starts.",
            var state                      => $"{state}…",
        } + (view.Problem is { } problem ? $" {problem}" : "");
        BtnStart.IsEnabled = view.State == CollectorServiceState.Stopped;
        BtnStop.IsEnabled  = view.State == CollectorServiceState.Running;

        Rows = store.Connections
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => Row(c, view))
            .ToList();
        ServersList.ItemsSource = Rows;

        if (view.RemoteProblem is { } remote)
            SettingsContext.ShowStatus(TxtRemoteStatus, remote, success: false);
        else if (view.RemotePath is { } path)
            SettingsContext.ShowStatus(TxtRemoteStatus,
                $"Reading it: {Count(view.Watches.Count(w => w.Value.By == $"the collector at {path}"))} of your servers matched by server and database.",
                success: null);
    }

    private static string Count(int n) => n == 1 ? "1" : n.ToString(System.Globalization.CultureInfo.CurrentCulture);

    private CollectorServerRow Row(ConnectionSettings connection, CollectorView view)
    {
        var id     = connection.Id;
        var handed = view.HandedOver.Contains(id);
        if (handed)
            return new CollectorServerRow(connection.DisplayName,
                view.Watches.TryGetValue(id, out var watch) && watch.NotWatching is { } why ? why : $"Watched by {CollectorLink.ServiceName}.",
                "Take back", $"Take {connection.DisplayName} back from the service", "SqlVitals watches it again, as its settings say.",
                new RelayCommand(() => _ = RunAsync(link => link.TakeBackAsync(id), TxtServersStatus,
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
            new RelayCommand(() => _ = RunAsync(link => link.HandOverAsync(id), TxtServersStatus,
                                                $"Handed {connection.DisplayName} to the service. Its Windows account needs a login on the server: see Collector Service in the docs.")),
            whyNot is null);
    }

    // A change to the service: said in the card's status line, never thrown.
    [ReportsItsErrors]
    private async Task RunAsync(Func<CollectorLink, Task> change, TextBlock status, string done)
    {
        if (_collectors is null)
            return;
        SettingsContext.ShowStatus(status, "Working…", success: null);
        try
        {
            await change(_collectors);
            SettingsContext.ShowStatus(status, done, success: true);
        }
        catch (Exception ex)
        {
            SettingsContext.ShowStatus(status, $"Could not: {SettingsContext.Redact(ex.Message)}", success: false);
        }
        Render();
    }

    private void CmbChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || CmbChoice.SelectedIndex < 0)
            return;
        var choice = (WatchChoice)CmbChoice.SelectedIndex;
        try
        {
            _context.Service.SetWatchChoice(choice);
            SettingsContext.ShowStatus(TxtChoiceStatus, choice switch
            {
                WatchChoice.App     => "Saved: ▶ Start watches the server in SqlVitals.",
                WatchChoice.Service => "Saved: ▶ Start hands the server to the collector service.",
                _                   => "Saved: ▶ Start asks where to watch the server.",
            }, success: true);
        }
        catch (Exception ex)
        {
            SettingsContext.ShowStatus(TxtChoiceStatus, $"Could not save the setting: {SettingsContext.Redact(ex.Message)}", success: false);
        }
    }

    private void SaveRemotePath()
    {
        try
        {
            _context.Service.SetCollectorHistoryPath(TxtRemotePath.Text);
            SettingsContext.ShowStatus(TxtRemoteStatus, TxtRemotePath.Text.Trim().Length == 0
                ? "Saved: no collector on another PC is read."
                : "Saved: reading it within a few seconds.", success: true);
            if (_collectors is not null)
                _ = RunAsync(link => link.RefreshAsync(), TxtRemoteStatus, "Read it.");
        }
        catch (Exception ex)
        {
            SettingsContext.ShowStatus(TxtRemoteStatus, $"Could not save the path: {SettingsContext.Redact(ex.Message)}", success: false);
        }
    }
}
