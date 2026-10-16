using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Home (#130, #189): every saved connection as a tile with its health score, the ones that need
/// attention first. Everything comes from the monitoring sessions and the local history; Home
/// never queries a server, and never contacts one that isn't monitored. Opening it and Refresh read
/// the tiles' last hour and day from the history; the monitoring's samples move them while shown.
/// </summary>
public sealed partial class HomeViewModel : PageViewModel
{
    private static readonly TimeSpan TrendBucket = TimeSpan.FromMinutes(1);

    /// <summary>How far back a tile says how much was monitored (#170), and the buckets it's read in.</summary>
    public static readonly TimeSpan CoverageSpan   = TimeSpan.FromHours(24);
    public static readonly TimeSpan CoverageBucket = TimeSpan.FromMinutes(5);

    private readonly MonitoringManager         _monitoring;
    private readonly ConnectionSettingsService _settings;
    private readonly IWatchStarter             _starter;
    private readonly IUiDispatcher             _ui;
    private readonly TimeProvider              _time;

    // The settings as of the last build. Re-read when they or monitoring change, not per sample:
    // reading means decrypting the file.
    private ConnectionStore _store = new();

    // A new order waits while the pointer is over the tiles, so one doesn't move from under a click.
    private bool _reorderPending;
    private bool _holdOrder;

    /// <param name="starter">What Start does (#172): asks whether the collector service should watch it.</param>
    /// <param name="ui">Settings saved elsewhere are told of on another thread: the tiles change on this one.</param>
    public HomeViewModel(MonitoringManager monitoring, ConnectionSettingsService settings, IWatchStarter starter, IUiDispatcher ui,
                         TimeProvider? time = null)
    {
        _monitoring = monitoring;
        _settings   = settings;
        _starter    = starter;
        _ui         = ui;
        _time       = time ?? TimeProvider.System;
        Build();
    }

    // ── What Home shows ───────────────────────────────────────────────────

    /// <summary>The tiles in the order shown; the view adds "+ Add a connection" after them.</summary>
    public ObservableCollection<HomeTile> Tiles { get; } = [];

    /// <summary>"3 connections · 2 monitored · sorted by most attention needed".</summary>
    [ObservableProperty]
    private string _headline = string.Empty;

    /// <summary>A count per status, on its status's colour: "2 critical".</summary>
    [ObservableProperty]
    private IReadOnlyList<Count> _counts = [];

    /// <summary>One of the counts at the top of Home.</summary>
    public sealed record Count(string Text, TileStatus Status);

    /// <summary>Why the tiles say little: nothing saved, or nothing monitored; empty otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsNote))]
    private string _note = string.Empty;

    public bool ShowsNote => Note.Length > 0;

    /// <summary>
    /// The pointer is over the tiles: a new order waits until it leaves, so a tile doesn't move
    /// from under a click. The view says so.
    /// </summary>
    public bool HoldOrder
    {
        get => _holdOrder;
        set
        {
            _holdOrder = value;
            if (!value && _reorderPending)
                Reorder();
        }
    }

    // ── Where its links go: the window opens them (#141, #145) ────────────

    /// <summary>Open was pressed on a tile, or Retry (which has already asked for a new sample).</summary>
    public event Action<Guid>? OpenRequested;

    /// <summary>A tile's Investigate: its worst active alert's evidence (#145).</summary>
    public event Action<Alert>? InvestigateRequested;

    /// <summary>"+ Add a connection" was pressed.</summary>
    public event Action? AddConnectionRequested;

    /// <summary>Edit connection… in a tile's menu (#141).</summary>
    public event Action<Guid>? EditRequested;

    // ── Reading ───────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the last hour of every tile's score from the history, for the sparklines, and the
    /// last day's, for how much of it was monitored (#170).
    /// </summary>
    protected override async Task LoadAsync(CancellationToken cancel)
    {
        Build();

        var reader  = _monitoring.HistoryReader;
        var ids     = Tiles.Select(t => t.Tile.Id).ToList();
        var toUtc   = _time.GetUtcNow().UtcDateTime;
        var fromUtc = toUtc - HomeTile.TrendSpan;

        IReadOnlyDictionary<Guid, (IReadOnlyList<ScorePoint> Hour, Coverage Day)> history;
        try
        {
            history = await Task.Run(() => ids.ToDictionary(
                id => id,
                id => ((IReadOnlyList<ScorePoint>)reader.ReadScores(id, fromUtc, toUtc, TrendBucket).Select(ScorePoint.From).ToList(),
                       Coverage.Of(reader.ReadScores(id, toUtc - CoverageSpan, toUtc, CoverageBucket).Select(p => p.TimeUtc),
                                   toUtc - CoverageSpan, toUtc, CoverageBucket))), cancel);
        }
        catch (HistorySchemaTooNewException)
        {
            // A newer SqlVitals wrote the file: the sparklines start from the live scores.
            return;
        }
        cancel.ThrowIfCancellationRequested();

        foreach (var tile in Tiles)
            if (history.TryGetValue(tile.Tile.Id, out var read))
                tile.SetHistory(read.Hour, read.Day);
    }

    // What moves the tiles is followed only while Home is shown.
    protected override void OnShowing()
    {
        _monitoring.SessionStateChanged += OnSessionStateChanged;
        _monitoring.MonitoringChanged   += Build;
        _settings.Changed               += OnSettingsChanged;
    }

    protected override void OnLeaving()
    {
        _monitoring.SessionStateChanged -= OnSessionStateChanged;
        _monitoring.MonitoringChanged   -= Build;
        _settings.Changed               -= OnSettingsChanged;
    }

    // ── Building and updating the tiles ───────────────────────────────────

    // Every saved connection's tile, from the settings and the sessions as they stand.
    private void Build()
    {
        var store = _store = _settings.Load();
        var now   = _time.GetLocalNow().DateTime;
        var built = store.Connections.Select(c => ServerTile.From(InputFor(c, store, _monitoring), now)).ToList();

        var byId = Tiles.ToDictionary(t => t.Tile.Id);
        foreach (var gone in Tiles.Where(t => built.All(b => b.Id != t.Tile.Id)).ToList())
            Tiles.Remove(gone);
        foreach (var tile in built)
        {
            if (byId.TryGetValue(tile.Id, out var existing))
                existing.Update(tile, _monitoring.Get(tile.Id)?.Score, WorstAlert(tile.Id));
            else
                Tiles.Add(new HomeTile(tile, _monitoring.Get(tile.Id)?.Score, WorstAlert(tile.Id), _time));
        }

        Reorder();
        UpdateSummary(store.Connections.Count);
        ShowBreakdown();
    }

    /// <summary>What Home and a server's Overview know about a connection, from its settings and session.</summary>
    internal static TileInput InputFor(ConnectionSettings conn, ConnectionStore store, MonitoringManager monitoring)
    {
        var session = monitoring.Get(conn.Id);
        return new TileInput(
            conn.Id, conn.DisplayName, conn.Server, conn.DatabaseLabel,
            IsActive:           conn.Id == store.ActiveConnectionId,
            IsMonitored:        session is { IsRunning: true },
            Score:              session?.Score,
            LastSuccess:        session?.LastSuccess,
            FailingSince:       session?.FailingSince,
            LastError:          session?.LastError,
            NotMonitoredReason: session is { IsRunning: true } ? null : monitoring.NotMonitoredReason(conn.Id),
            CanStart:           monitoring.CanStart(conn.Id),
            // A collector watches it (#172): the tile says what the collector's history says.
            Elsewhere:          session is { IsRunning: true } ? null : monitoring.WatchFor(conn.Id));
    }

    // A sample, a failure, or a start or stop of one connection: only its tile changes.
    private void OnSessionStateChanged(Guid connectionId)
    {
        var tile = Tiles.FirstOrDefault(t => t.Tile.Id == connectionId);
        var conn = _store.Connections.FirstOrDefault(c => c.Id == connectionId);
        if (tile is null || conn is null)
        {
            Build();
            return;
        }

        tile.Update(ServerTile.From(InputFor(conn, _store, _monitoring), _time.GetLocalNow().DateTime),
                    _monitoring.Get(connectionId)?.Score, WorstAlert(connectionId));
        if (BreakdownFor == connectionId)
            ShowBreakdown();
        Reorder();
        UpdateSummary(_store.Connections.Count);
        ReportUpdated();
    }

    // A connection added, removed, renamed or made active; told after the save has been applied.
    private void OnSettingsChanged(IReadOnlyList<ActivityEntry> rows) => _ui.Post(Build);

    // Moves the tiles into the order they should be in, unless the pointer is over them.
    private void Reorder()
    {
        var order = TileOrder.Sort(Tiles.Select(t => t.Tile)).Select(t => t.Id).ToList();
        if (order.SequenceEqual(Tiles.Select(t => t.Tile.Id)))
        {
            _reorderPending = false;
            return;
        }
        if (HoldOrder)
        {
            _reorderPending = true;
            return;
        }

        _reorderPending = false;
        for (var i = 0; i < order.Count; i++)
        {
            var from = Tiles.IndexOf(Tiles.First(t => t.Tile.Id == order[i]));
            if (from != i)
                Tiles.Move(from, i);
        }
    }

    private void UpdateSummary(int connections)
    {
        var summary = HomeSummary.From(Tiles.Select(t => t.Tile).ToList());
        Headline = summary.Headline;
        Counts   = summary.Counts.Select(c => new Count(c.Text, c.Status)).ToList();
        Note     = connections == 0
            ? "No saved connections yet. Add one to see its health here."
            : summary.Monitored == 0
                ? "Nothing is monitored, so no scores are being taken. Start a connection from its tile's ⋯ menu or " +
                  "Start button, or turn on Start monitoring automatically in Settings."
                : string.Empty;
    }

    // The alert a tile's Investigate opens (#145): the worst active one, the oldest first.
    private Alert? WorstAlert(Guid connectionId) =>
        (_monitoring.Get(connectionId)?.ActiveAlerts ?? [])
            .OrderByDescending(a => a.Severity)
            .ThenBy(a => a.StartedUtc)
            .FirstOrDefault();

    // ── A tile's buttons ──────────────────────────────────────────────────

    [RelayCommand]
    private void Investigate(HomeTile tile)
    {
        if (tile.Alert is { } alert)
            InvestigateRequested?.Invoke(alert);
    }

    /// <summary>A tile's button: Start, Retry or Open, as the tile says.</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ActOnTile(HomeTile tile) => ReportErrorsOf(ActAsync(tile.Tile));

    private async Task ActAsync(ServerTile tile)
    {
        switch (tile.Action)
        {
            case TileAction.Start:
                await _starter.StartAsync(tile.Id, ActivitySources.Home);
                break;

            case TileAction.Retry:
                if (_monitoring.Get(tile.Id) is { } session)
                    _ = RetryAsync(session);
                OpenRequested?.Invoke(tile.Id);
                break;

            default:
                OpenRequested?.Invoke(tile.Id);
                break;
        }
    }

    [RelayCommand]
    private void AddConnection() => AddConnectionRequested?.Invoke();

    // A failure is already in the session's state, which the tile and the Overview show.
    internal static async Task RetryAsync(MonitoringSession session)
    {
        try { await session.CollectNowAsync(); }
        catch (Exception ex) { AppLog.Info($"Home: retry failed: {ConnectionSettingsService.RedactSecrets(ex.Message)}"); }
    }

    // ── The tile's menu (#141) ────────────────────────────────────────────

    /// <summary>One choice of a tile's menu; null in <see cref="MenuFor"/> is a separator.</summary>
    public sealed record MenuChoice(string Header, bool IsEnabled, string? ToolTip, Action Choose);

    /// <summary>The ⋯ on a tile was pressed: the view opens <see cref="MenuFor"/> beside it.</summary>
    public event Action<HomeTile>? MenuRequested;

    [RelayCommand]
    private void ShowMenu(HomeTile tile) => MenuRequested?.Invoke(tile);

    /// <summary>
    /// Open, Start or Stop monitoring, Why this score, Edit connection: what the sidebar's
    /// connection selector offered for each server, now that the sidebar shows only the open one.
    /// </summary>
    public IReadOnlyList<MenuChoice?> MenuFor(HomeTile tile)
    {
        var id         = tile.Tile.Id;
        var monitoring = _monitoring.IsMonitoring(id);
        return
        [
            new("Open", true, null, () => OpenRequested?.Invoke(id)),
            new(monitoring ? "Stop monitoring" : "Start monitoring",
                monitoring || _monitoring.CanStart(id),
                monitoring ? "Stop monitoring this connection until SqlVitals exits. Its active alerts end."
                           : "Start monitoring this connection until SqlVitals exits, without switching to it.",
                () =>
                {
                    if (monitoring) _monitoring.Stop(id, ActivitySources.Home);
                    else            _ = _starter.StartAsync(id, ActivitySources.Home);
                }),
            new("Why this score…", _monitoring.Get(id) is { Score: not null }, null, () => BreakdownFor = id),
            null,
            new("Edit connection…", true, null, () => EditRequested?.Invoke(id)),
        ];
    }

    /// <summary>The connection whose score's breakdown is open (Why this score…); kept current as its samples arrive.</summary>
    public Guid? BreakdownFor
    {
        get => _breakdownFor;
        set
        {
            if (SetProperty(ref _breakdownFor, value))
                ShowBreakdown();
        }
    }

    private Guid? _breakdownFor;

    /// <summary>What the breakdown shows, for <see cref="BreakdownFor"/>; null while it is closed.</summary>
    [ObservableProperty]
    private ScoreBreakdown? _breakdown;

    /// <summary>A score's breakdown (#36): whose, the score, what to say without one, and whether monitoring is paused.</summary>
    public sealed record ScoreBreakdown(string Name, HealthScore? Score, string Text, bool Paused);

    private void ShowBreakdown()
    {
        if (BreakdownFor is not { } id || Tiles.FirstOrDefault(t => t.Tile.Id == id) is not { } tile)
        {
            Breakdown = null;
            return;
        }
        var session = _monitoring.Get(id);
        Breakdown = new ScoreBreakdown(tile.Tile.Name, session?.Score,
                                       session is null ? _monitoring.NotMonitoredReason(id) : session.HealthText,
                                       Paused: session is { IsRunning: false });
    }
}
