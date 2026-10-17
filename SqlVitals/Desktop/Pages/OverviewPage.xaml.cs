using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// One server's Overview (#143), bound to <see cref="OverviewViewModel"/> (#189): the code-behind
/// hands the time window to the top bar and its picks to the view model, lays the charts out to
/// the width, and draws them and their legends in the theme's colours.
/// </summary>
public partial class OverviewPage : Page, IPageContext
{
    internal OverviewViewModel ViewModel { get; }

    public OverviewPage(OverviewViewModel viewModel)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        RangePicker.OffersBaseline = false;
        RangePicker.SetHistoryAvailable(true);
        BuildLegends();
        DrawCharts();

        SizeChanged += (_, e) => ChartsGrid.Columns = e.NewSize.Width < 1000 ? 2 : 3;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(OverviewViewModel.Charts))
                DrawCharts();
        };
    }

    // ── The top bar (#140) ────────────────────────────────────────────────────
    // Live while the charts follow the samples, History while a past range is picked.

    public PageKind Kind => ViewModel.IsLive ? PageKind.Live : PageKind.History;
    public TimeSpan? LiveInterval => ViewModel.LiveInterval;
    public bool IsPaused => ViewModel.IsStopped;
    public DateTime? DataTo => ViewModel.DataTo;

    // The time window goes to the top bar, without a baseline: the Overview compares nothing.
    public FrameworkElement? TakeBarTools() => PageBar.Detach(RangePicker);

    // Connected from XAML by name: a generic event handler is attached through reflection.
    [SuppressMessage("Style", "IDE0051", Justification = "Connected from XAML")]
    [SuppressMessage("Style", "IDE0060", Justification = "Event handler signature")]
    private async void RangePicker_RangeChanged(object? sender, HistoryRange range) =>
        await ViewModel.ReportErrorsOf(ViewModel.PickRangeAsync(range));

    // ── Its server ────────────────────────────────────────────────────────────

    public Guid ConnectionId => ViewModel.ConnectionId;

    // ── The charts, in the theme's colours ────────────────────────────────────

    // The theme's colours by meaning, the same as Live charts' (#164).
    private Brush WaitCpu      => Theme("WaitCpu");
    private Brush WaitIo       => Theme("WaitIo");
    private Brush WaitLock     => Theme("WaitLock");
    private Brush WaitMemory   => Theme("WaitMemory");
    private Brush WaitNetwork  => Theme("WaitNetwork");
    private Brush Batches      => Theme("MetricBatches");
    private Brush Transactions => Theme("MetricTransactions");
    private Brush Ple          => Theme("MetricPle");
    private Brush Reads        => Theme("MetricReads");
    private Brush Writes       => Theme("MetricWrites");

    private Brush Theme(string brushKey) => (Brush)FindResource(brushKey);

    private Brush Accent => TryFindResource("Accent") as Brush ?? Brushes.MediumPurple;

    // Each chart's title, with a coloured dot per line.
    private void BuildLegends()
    {
        Legend(LegendCpu, "CPU (%)", ("SQL Server", Accent));
        // Other leaves out what the categories don't cover, as Live charts does by default (#136).
        Legend(LegendWaits, "Waits (ms/s)", ("CPU", WaitCpu), ("I/O", WaitIo), ("Locks", WaitLock), ("Memory", WaitMemory), ("Network", WaitNetwork));
        Legend(LegendThroughput, "Throughput (/s)", ("Batch requests", Batches), ("Transactions", Transactions));
        Legend(LegendPle, "Page life expectancy (s)", ("PLE", Ple));
        Legend(LegendIo, "Physical I/O (pages/s)", ("Reads", Reads), ("Writes", Writes));
    }

    private static void Legend(TextBlock block, string title, params (string Name, Brush Brush)[] series)
    {
        block.Inlines.Clear();
        block.Inlines.Add(new Run(title));
        if (series.Length < 2)
            return;
        foreach (var (name, brush) in series)
        {
            block.Inlines.Add(new Run("   ●") { Foreground = brush, FontWeight = FontWeights.Normal });
            block.Inlines.Add(new Run($" {name}") { FontWeight = FontWeights.Normal, FontSize = 11 });
        }
    }

    // What the view model says to draw: the scores, a chart per group of metrics, and the alerts' starts.
    private void DrawCharts()
    {
        var charts = ViewModel.Charts;
        var (fromUtc, toUtc, bucket) = (charts.FromUtc, charts.ToUtc, charts.Bucket);
        var markers = charts.Markers.Select(m => new ChartMarker(m.TimeUtc, m.Label, HealthBrushes.For(m.Severity))).ToList();
        MetricSeries Series(string name, Brush brush, Func<LiveMetricSample, double> value) =>
            new(name, brush, charts.Samples.Select(r => new MetricPoint(r.TimeUtc, value(r.Sample))).ToList());

        ChartScore.Data = new ScoreChartData(charts.Scores, fromUtc, toUtc, bucket, markers);
        ChartCpu.Data   = new MetricChartData([Series("SQL Server CPU", Accent, s => s.SqlCpuPct)], fromUtc, toUtc, bucket, markers, Top: 100);
        ChartWaits.Data = new MetricChartData(
        [
            Series("CPU",     WaitCpu,     s => s.WaitCpuMsPerSec),
            Series("I/O",     WaitIo,      s => s.WaitIoMsPerSec),
            Series("Locks",   WaitLock,    s => s.WaitLockMsPerSec),
            Series("Memory",  WaitMemory,  s => s.WaitMemoryMsPerSec),
            Series("Network", WaitNetwork, s => s.WaitNetworkMsPerSec),
        ], fromUtc, toUtc, bucket, markers);
        ChartThroughput.Data = new MetricChartData(
        [
            Series("Batch requests", Batches, s => s.BatchRequestsPerSec),
            Series("Transactions",   Transactions, s => s.TransactionsPerSec),
        ], fromUtc, toUtc, bucket, markers);
        ChartPle.Data = new MetricChartData([Series("Page life expectancy", Ple, s => s.PageLifeExpectancySec)], fromUtc, toUtc, bucket, markers);
        ChartIo.Data  = new MetricChartData(
        [
            Series("Reads",  Reads,  s => s.PhysicalReadsPerSec),
            Series("Writes", Writes, s => s.PhysicalWritesPerSec),
        ], fromUtc, toUtc, bucket, markers);
    }
}
