using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Home;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// A Home tile (#130): the <see cref="ServerTile"/> with its last hour of scores. Replaced in place
/// as samples arrive, so the tile keeps its element and only redraws. Its colours are the view's,
/// from <see cref="ServerTile.Status"/> and each bar's level (#189).
/// </summary>
public sealed class HomeTile : INotifyPropertyChanged
{
    /// <summary>How far back a tile's sparkline goes.</summary>
    public static readonly TimeSpan TrendSpan = TimeSpan.FromHours(1);

    private IReadOnlyList<ScorePoint> _history = [];
    private readonly List<ScorePoint> _live = new();
    private readonly TimeProvider _time;

    /// <param name="time">The clock its last hour runs to: its view model's.</param>
    public HomeTile(ServerTile tile, HealthScore? score, Alert? alert = null, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        Tile  = tile;
        Alert = alert;
        AddLive(score);
    }

    private DateTime NowUtc => _time.GetUtcNow().UtcDateTime;

    public ServerTile Tile { get; private set; }

    /// <summary>The server's worst active alert, whose evidence Investigate opens (#145); null when none.</summary>
    public Alert? Alert { get; private set; }

    public bool ShowsInvestigate => Alert is not null;

    public string InvestigateToolTip => Alert is { } alert
        ? $"{HealthThresholds.Info(alert.Indicator).Name} · {AlertText.Severity(alert.Severity).ToLowerInvariant()}: open {EvidenceRoutes.For(alert.Indicator).Text}, filtered to the alert"
        : string.Empty;

    public string InvestigateName => Alert is { } alert
        ? $"Investigate the {HealthThresholds.Info(alert.Indicator).Name} alert on {Tile.Name}"
        : string.Empty;

    /// <summary>Monitored, or waiting for its first sample: the score and its bars show; off, why not and Start.</summary>
    public bool ShowsScore => Tile.Status != TileStatus.Off;

    public bool IsOff => Tile.Status == TileStatus.Off;

    public string? ReasonToolTip => Tile.ReasonToolTip ?? Tile.Reason;

    /// <summary>"More for UAT": the tile's menu.</summary>
    public string MenuName => $"More for {Tile.Name}";

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
        TileAction.Retry => "Try to reach the server now, and open its Overview",
        _                => "Open this server's details: its score over time, its checks and its alerts",
    };

    public IReadOnlyList<Bar> Bars => Tile.Bars.Select(b => new Bar(b.Label, b.Level, b.ToolTip)).ToList();

    /// <summary>One of a tile's bars: its check's short name, level and tip.</summary>
    public sealed record Bar(string Label, HealthLevel Level, string ToolTip);

    /// <summary>The last hour of scores: from the history, then the live ones since.</summary>
    public ScoreChartData Trend
    {
        get
        {
            var to = NowUtc;
            return new ScoreChartData(ScoreTrend.Merge(_history, _live), to - TrendSpan, to, TimeSpan.FromMinutes(1));
        }
    }

    [SuppressMessage("Performance", "CA1822", Justification = "Bound in XAML; a binding needs an instance member")]
    public string TrendToolTip => "Health score over the last hour. Grey: not monitored. Red: the server couldn't be reached.";

    /// <summary>"Monitored 14 h of the last 24 h" (#170); empty until the history is read.</summary>
    public string CoverageText => Coverage?.Text() ?? string.Empty;

    /// <summary>The longest gap, when there was one.</summary>
    public string? CoverageToolTip => Coverage?.LongestGapText(t => t.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture));

    /// <summary>How much of the last day was monitored (#170); null until the history is read.</summary>
    public Coverage? Coverage { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(ServerTile tile, HealthScore? score, Alert? alert = null)
    {
        Tile  = tile;
        Alert = alert;
        AddLive(score);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }

    public void SetHistory(IReadOnlyList<ScorePoint> history, Coverage? coverage = null)
    {
        _history = history;
        Coverage = coverage ?? Coverage;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Trend)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverageText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CoverageToolTip)));
    }

    // Each new score once; none older than the sparkline shows.
    private void AddLive(HealthScore? score)
    {
        if (score is null || Tile.Status == TileStatus.Off)
            return;

        var at = score.Time.ToUniversalTime();
        if (_live.Count == 0 || at > _live[^1].TimeUtc)
            _live.Add(new ScorePoint(at, score.Value, score.Level != HealthLevel.Unavailable));
        _live.RemoveAll(p => p.TimeUtc < NowUtc - TrendSpan);
    }
}
