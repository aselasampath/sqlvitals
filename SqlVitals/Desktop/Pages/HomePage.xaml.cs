using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Home (#130): every saved connection as a tile with its health score, the ones that need
/// attention first. Everything comes from the monitoring sessions and the local history; Home
/// never queries a server, and never contacts one that isn't monitored.
/// </summary>
public partial class HomePage : Page, IRefreshable, IPageContext
{
    // Every monitored server's health as its samples arrive (#140); each has its own interval.
    public PageKind Kind => PageKind.Live;

    /// <summary>How far back a tile's sparkline goes.</summary>
    public static readonly TimeSpan TrendSpan = TimeSpan.FromHours(1);

    private static readonly TimeSpan TrendBucket = TimeSpan.FromMinutes(1);

    // The narrowest a tile gets before the grid drops a column.
    private const double MinTileWidth = 300;

    public static readonly DependencyProperty TileWidthProperty =
        DependencyProperty.Register(nameof(TileWidth), typeof(double), typeof(HomePage), new PropertyMetadata(MinTileWidth + 14));

    private readonly MonitoringManager         _monitoring;
    private readonly ConnectionSettingsService _settings;
    private readonly ObservableCollection<HomeTile> _tiles = new();
    private readonly Button _addTile;

    // A new order waits while the mouse is over the tiles, so one doesn't move from under a click.
    private bool _reorderPending;

    // Bumped per history load; an earlier, slower load's result is dropped.
    private int _loadVersion;

    // The settings as of the last build. Re-read when they or monitoring change, not per sample:
    // reading means decrypting the file.
    private ConnectionStore _store = new();

    public HomePage(MonitoringManager monitoring, ConnectionSettingsService settings)
    {
        _monitoring = monitoring;
        _settings   = settings;
        InitializeComponent();

        _addTile = new Button
        {
            Content   = "+ Add a connection",
            Margin    = new Thickness(0, 0, 14, 14),
            MinHeight = 236,
            ToolTip   = "Opens Settings to add a SQL Server connection",
        };
        _addTile.Style = (Style)Resources["AddTile"];
        _addTile.Click += (_, _) => AddConnectionRequested?.Invoke();
        Tiles.ItemsSource = new CompositeCollection
        {
            new CollectionContainer { Collection = _tiles },
            _addTile,
        };

        Build();

        SizeChanged += (_, _) => UpdateTileWidth();
        Loaded += (_, _) =>
        {
            _monitoring.SessionStateChanged += OnSessionStateChanged;
            _monitoring.MonitoringChanged   += Build;
            _settings.Changed               += OnSettingsChanged;
        };
        Unloaded += (_, _) =>
        {
            _monitoring.SessionStateChanged -= OnSessionStateChanged;
            _monitoring.MonitoringChanged   -= Build;
            _settings.Changed               -= OnSettingsChanged;
        };
    }

    /// <summary>Open was pressed on a tile, or Retry (which has already asked for a new sample).</summary>
    public event Action<Guid>? OpenRequested;

    /// <summary>"+ Add a connection" was pressed.</summary>
    public event Action? AddConnectionRequested;

    /// <summary>The width of a tile's slot, gap included, so the tiles fill the row.</summary>
    public double TileWidth
    {
        get => (double)GetValue(TileWidthProperty);
        set => SetValue(TileWidthProperty, value);
    }

    /// <summary>The tiles in the order shown, without "+ Add a connection".</summary>
    internal IReadOnlyList<HomeTile> TilesShown => _tiles;

    /// <summary>Reads the last hour of every tile's score from the history, for the sparklines.</summary>
    public async Task RefreshAsync()
    {
        Build();

        var version = ++_loadVersion;
        var reader  = _monitoring.HistoryReader;
        var ids     = _tiles.Select(t => t.Tile.Id).ToList();
        var toUtc   = DateTime.UtcNow;
        var fromUtc = toUtc - TrendSpan;

        IReadOnlyDictionary<Guid, IReadOnlyList<ScorePoint>> history;
        try
        {
            history = await Task.Run(() => ids.ToDictionary(
                id => id,
                id => (IReadOnlyList<ScorePoint>)reader.ReadScores(id, fromUtc, toUtc, TrendBucket).Select(ScorePoint.From).ToList()));
        }
        catch (HistorySchemaTooNewException)
        {
            // A newer SqlVitals wrote the file: the sparklines start from the live scores.
            return;
        }
        if (version != _loadVersion)
            return;

        foreach (var tile in _tiles)
            if (history.TryGetValue(tile.Tile.Id, out var points))
                tile.SetHistory(points);
    }

    // ── Building and updating the tiles ───────────────────────────────────────

    // Every saved connection's tile, from the settings and the sessions as they stand.
    private void Build()
    {
        var store = _store = _settings.Load();
        var now   = DateTime.Now;
        var built = store.Connections.Select(c => ServerTile.From(InputFor(c, store), now)).ToList();

        var byId = _tiles.ToDictionary(t => t.Tile.Id);
        foreach (var gone in _tiles.Where(t => built.All(b => b.Id != t.Tile.Id)).ToList())
            _tiles.Remove(gone);
        foreach (var tile in built)
        {
            if (byId.TryGetValue(tile.Id, out var existing))
                existing.Update(tile, _monitoring.Get(tile.Id)?.Score);
            else
                _tiles.Add(new HomeTile(tile, _monitoring.Get(tile.Id)?.Score));
        }

        Reorder();
        UpdateSummary(store.Connections.Count);
    }

    private TileInput InputFor(ConnectionSettings conn, ConnectionStore store) => InputFor(conn, store, _monitoring);

    /// <summary>What Home and the server detail page know about a connection, from its settings and session.</summary>
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
            CanStart:           monitoring.CanStart(conn.Id));
    }

    // A sample, a failure, or a start or stop of one connection: only its tile changes.
    private void OnSessionStateChanged(Guid connectionId)
    {
        var tile = _tiles.FirstOrDefault(t => t.Tile.Id == connectionId);
        var conn = _store.Connections.FirstOrDefault(c => c.Id == connectionId);
        if (tile is null || conn is null)
        {
            Build();
            return;
        }

        tile.Update(ServerTile.From(InputFor(conn, _store), DateTime.Now), _monitoring.Get(connectionId)?.Score);
        Reorder();
        UpdateSummary(_store.Connections.Count);
        MainWindow.ReportBackgroundSuccess(this);
    }

    // A connection added, removed, renamed or made active. After the save has been applied.
    private void OnSettingsChanged(IReadOnlyList<ActivityEntry> rows) => Dispatcher.InvokeAsync(Build);

    // Moves the tiles into the order they should be in, unless the mouse is over them.
    private void Reorder()
    {
        var order = TileOrder.Sort(_tiles.Select(t => t.Tile)).Select(t => t.Id).ToList();
        if (order.SequenceEqual(_tiles.Select(t => t.Tile.Id)))
        {
            _reorderPending = false;
            return;
        }
        if (Tiles.IsMouseOver)
        {
            _reorderPending = true;
            return;
        }

        _reorderPending = false;
        for (var i = 0; i < order.Count; i++)
        {
            var from = _tiles.IndexOf(_tiles.First(t => t.Tile.Id == order[i]));
            if (from != i)
                _tiles.Move(from, i);
        }
    }

    private void Tiles_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_reorderPending)
            Reorder();
    }

    private void UpdateSummary(int connections)
    {
        var summary = HomeSummary.From(_tiles.Select(t => t.Tile).ToList());
        TxtHeadline.Text   = summary.Headline;
        Counts.ItemsSource = summary.Counts.Select(c => new HomeCount(c.Text, HealthBrushes.Tint(c.Status))).ToList();

        TxtNote.Text = connections == 0
            ? "No saved connections yet. Add one to see its health here."
            : summary.Monitored == 0
                ? "Nothing is monitored, so no scores are being taken. Start a connection here or with ▶ in the sidebar " +
                  "selector, or turn on Start monitoring automatically in Settings."
                : string.Empty;
        TxtNote.Visibility = TxtNote.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateTileWidth()
    {
        // The tiles sit inside the page's margins and scrollbar.
        var width   = Math.Max(0, ActualWidth - 20 - 6 - SystemParameters.VerticalScrollBarWidth);
        var columns = Math.Max(1, (int)((width + 0.5) / (MinTileWidth + 14)));
        TileWidth   = Math.Floor(width / columns);
    }

    // ── Buttons ───────────────────────────────────────────────────────────────

    private void TileAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: HomeTile { Tile: var tile } })
            return;

        switch (tile.Action)
        {
            case TileAction.Start:
                _monitoring.Start(tile.Id, ActivitySources.Home);
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

    // A failure is already in the session's state, which the tile and detail page show.
    internal static async Task RetryAsync(MonitoringSession session)
    {
        try { await session.CollectNowAsync(); }
        catch (Exception ex) { AppLog.Info($"Home: retry failed: {ConnectionSettingsService.RedactSecrets(ex.Message)}"); }
    }
}

/// <summary>A count at the top of Home, on its status's colour: "2 critical".</summary>
public sealed record HomeCount(string Text, Brush Tint);

/// <summary>One of a tile's six bars.</summary>
public sealed record HomeBar(string Label, Brush Brush, string ToolTip);

/// <summary>
/// A Home tile (#130): the <see cref="ServerTile"/> with its colours and its last hour of scores.
/// Replaced in place as samples arrive, so the tile keeps its element and only redraws.
/// </summary>
public sealed class HomeTile : INotifyPropertyChanged
{
    private IReadOnlyList<ScorePoint> _history = [];
    private readonly List<ScorePoint> _live = new();

    public HomeTile(ServerTile tile, HealthScore? score)
    {
        Tile = tile;
        AddLive(score);
    }

    public ServerTile Tile { get; private set; }

    public Brush StatusBrush => HealthBrushes.For(Tile.Status);
    public Brush StatusTint  => HealthBrushes.Tint(Tile.Status);

    public Visibility ActiveVisibility => Tile.IsActive ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ScoreVisibility  => Tile.Status == TileStatus.Off ? Visibility.Collapsed : Visibility.Visible;
    public Visibility OffVisibility    => Tile.Status == TileStatus.Off ? Visibility.Visible : Visibility.Collapsed;

    public string? ReasonToolTip => Tile.ReasonToolTip ?? Tile.Reason;

    /// <summary>"Open UAT", "Retry DR replica", "Start monitoring localhost".</summary>
    public string ActionName => Tile.Action switch
    {
        TileAction.Start => $"Start monitoring {Tile.Name}",
        TileAction.Retry => $"Retry {Tile.Name}",
        _                => $"Open {Tile.Name}",
    } + $" ({Tile.AutomationName})";

    public string ActionToolTip => Tile.Action switch
    {
        TileAction.Start when !Tile.CanAct => $"Can't start: {Tile.Footer}",
        TileAction.Start => "Start monitoring this connection until SqlVitals exits, without switching to it.",
        TileAction.Retry => "Try to reach the server now, and open its details",
        _                => "Open this server's details: its score over time, its checks and its alerts",
    };

    public IReadOnlyList<HomeBar> Bars => Tile.Bars
        .Select(b => new HomeBar(b.Label, HealthBrushes.For(b.Level), b.ToolTip))
        .ToList();

    /// <summary>The last hour of scores: from the history, then the live ones since.</summary>
    public ScoreChartData Trend
    {
        get
        {
            var to = DateTime.UtcNow;
            return new ScoreChartData(ScoreTrend.Merge(_history, _live), to - HomePage.TrendSpan, to, TimeSpan.FromMinutes(1));
        }
    }

    public string TrendToolTip => "Health score over the last hour";

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(ServerTile tile, HealthScore? score)
    {
        Tile = tile;
        AddLive(score);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public void SetHistory(IReadOnlyList<ScorePoint> history)
    {
        _history = history;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Trend)));
    }

    // Each new score once; none older than the sparkline shows.
    private void AddLive(HealthScore? score)
    {
        if (score is null || Tile.Status == TileStatus.Off)
            return;

        var at = score.Time.ToUniversalTime();
        if (_live.Count == 0 || at > _live[^1].TimeUtc)
            _live.Add(new ScorePoint(at, score.Value, score.Level != HealthLevel.Unavailable));
        _live.RemoveAll(p => p.TimeUtc < DateTime.UtcNow - HomePage.TrendSpan);
    }
}
