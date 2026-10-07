using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Engine.Repositories;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Models;

namespace SqlVitals.Desktop.Pages;

public partial class TempDbPage : System.Windows.Controls.Page, IRefreshable, IPageContext
{
    // Read once, and again on Refresh (#140).
    public PageKind Kind => PageKind.Snapshot;

    private readonly ITempDbRepository _repo;

    // The page template's verdict (#150): who holds TempDB, and how much room is left.
    internal TempDbViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's, the counters' and the chart's folds are remembered; none in some tests.</param>
    public TempDbPage(ITempDbRepository repo, ISectionFolds? folds = null)
    {
        _repo = repo;
        InitializeComponent();

        ViewModel = new TempDbViewModel(showSession: ShowTopSession);
        ViewModel.Attach(Shell);
        Shell.Folds = folds;
    }

    // The verdict's Show the session: the one holding the most, selected and in view.
    private void ShowTopSession()
    {
        if (SessionsGrid.Items.OfType<TempDbSession>().MaxBy(s => s.NetTempDBUsageKB) is not { } top)
            return;
        SessionsGrid.SelectedItem = top;
        SessionsGrid.ScrollIntoView(top);
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        List<TempDbFile> fileList;
        List<TempDbSession> sessionList;
        List<TempDbCounter> counterList;
        try
        {
            var (files, sessions, counters) = await _repo.GetTempDbPressureAsync();
            (fileList, sessionList, counterList) = (files.ToList(), sessions.ToList(), counters.ToList());
        }
        catch (Exception ex)
        {
            ViewModel.ShowError("TempDB", ex);
            throw;
        }
        ViewModel.Show(fileList, sessionList, counterList);

        // Folded, the counters and the chart say it in one line (#150).
        CountersSection.FoldedSummary = counterList.Count == 0 ? "No TempDB counters were read"
            : string.Join(" · ", counterList.Take(3).Select(c => $"{c.CounterName.Trim()} {c.CounterValue:N0}"));
        FilesSection.FoldedSummary = fileList.Count == 0 ? "No TempDB files were read"
            : $"{fileList.Count} file{(fileList.Count == 1 ? "" : "s")} · {fileList.Sum(f => f.SpaceUsedMB):N0} MB used of {fileList.Sum(f => f.FileSizeMB):N0} MB";

        // ── Counter KPI cards ──────────────────────────────────────────
        CounterGrid.Children.Clear();
        foreach (var c in counterList)
        {
            double val = c.CounterValue;
            string brushKey = c.CounterName.Contains("Version") && val > 0 ? "Danger"
                            : c.CounterName.Contains("Free")    && val < 1_000_000 ? "Warning" // < ~1 GB free (counter is in KB)
                            : "Accent";
            string tip = c.CounterName switch
            {
                var n when n.Contains("Version Store Size")          => "Size (KB) of the version store in TempDB used by snapshot/RCSI isolation. Rapid growth indicates long-running read-committed snapshot transactions that keep row versions alive. The Performance Counters page charts how fast versions are generated and cleaned up.",
                var n when n.Contains("Free Space in tempdb")        => "Free space remaining in TempDB (KB). Low free space can cause query failures and blocking. Monitor alongside auto-grow events.",
                var n when n.Contains("Active Transactions")         => "Transactions open in TempDB right now. A long-running one keeps its row versions, and everything since, in the version store.",
                var n when n.Contains("Active Temp Tables")          => "Number of temporary tables and table variables currently open across all sessions. Very high counts may indicate connection-per-request designs or missing cleanup logic.",
                var n when n.Contains("Temp Tables For Destruction") => "Temporary tables waiting for the cleanup thread to drop them. A number that keeps climbing means cleanup can't keep up.",
                _                                                     => $"TempDB performance counter from sys.dm_os_performance_counters: {c.CounterName}."
            };
            AddKpiCard(c.CounterName, $"{val:N0}", brushKey, tip);
        }

        // ── File space bar chart ───────────────────────────────────────
        if (fileList.Count > 0)
        {
            var labels   = fileList.Select(f => f.FileName).ToArray();
            var usedVals = fileList.Select(f => (double)f.SpaceUsedMB).ToArray();
            var freeVals = fileList.Select(f => (double)f.FreeSpaceMB).ToArray();

            var axisColor = ChartTheme.MutedAxisColor;
            var gridColor = ChartTheme.GridColor;

            FileChart.Series = new ISeries[]
            {
                new StackedColumnSeries<double>
                {
                    Values      = usedVals,
                    Name        = "Used MB",
                    Fill        = new SolidColorPaint(ChartTheme.Of("DangerColor")),
                    Stroke      = null,
                    MaxBarWidth = 60,
                    Padding     = 4,
                },
                new StackedColumnSeries<double>
                {
                    Values      = freeVals,
                    Name        = "Free MB",
                    Fill        = new SolidColorPaint(ChartTheme.Of("SuccessColor")),
                    Stroke      = null,
                    MaxBarWidth = 60,
                    Padding     = 4,
                },
            };
            FileChart.XAxes = new[] { new Axis
            {
                Labels          = labels,
                LabelsPaint     = new SolidColorPaint(axisColor),
                SeparatorsPaint = new SolidColorPaint(gridColor) { StrokeThickness = 1 },
                TextSize        = 11,
            }};
            FileChart.YAxes = new[] { new Axis
            {
                LabelsPaint     = new SolidColorPaint(axisColor),
                SeparatorsPaint = new SolidColorPaint(gridColor) { StrokeThickness = 1 },
                TextSize        = 11,
            }};
        }

        // ── Sessions DataGrid ──────────────────────────────────────────
        SessionsGrid.ItemsSource = sessionList.OrderByDescending(s => s.InternalObjAllocKB + s.UserObjAllocKB);
    }

    private void AddKpiCard(string label, string value, string brushKey, string? tooltip = null)
    {
        var card  = new Border
        {
            Style  = (Style)Application.Current.FindResource("KpiCard"),
            Margin = new Thickness(0, 0, 10, 10),
            Width  = 200,
        };
        if (tooltip is not null)
            ToolTipService.SetToolTip(card, MakeTooltip(label, tooltip));
        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = value, Style = (Style)Application.Current.FindResource("KpiValue") }.WithForeground(brushKey));
        sp.Children.Add(new TextBlock { Text = label, Style = (Style)Application.Current.FindResource("KpiLabel") });
        card.Child = sp;
        CounterGrid.Children.Add(card);
    }

    private static ToolTip MakeTooltip(string title, string description)
    {
        var sp = new StackPanel { MaxWidth = 340 };
        sp.Children.Add(new TextBlock { Text = title,       FontWeight = FontWeights.Bold,   TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,4) });
        sp.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        return new ToolTip { Content = sp };
    }
}
