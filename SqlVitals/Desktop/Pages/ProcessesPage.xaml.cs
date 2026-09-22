using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

public partial class ProcessesPage : Page, IRefreshable
{
    private const double BallRadius = 22;
    private const double BallDiam   = BallRadius * 2;

    private readonly IWaitStatsRepository _repo;
    private List<ProcessNode> _allNodes  = [];
    private string            _searchText = "";

    // ?? Auto-refresh ??????????????????????????????????????????????????
    private readonly DispatcherTimer _timer = new();
    private int _intervalSec   = 10;   // default selection
    private int _remainingSec  = 0;
    private bool _refreshing   = false;

    // Interval choices: 5 s increments up to 120 s
    private static readonly int[] Intervals =
        Enumerable.Range(1, 24).Select(i => i * 5).ToArray(); // 5,10,15,...,120

    public ProcessesPage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();

        // Populate interval ComboBox
        var darkItemStyle = (Style)FindResource("DarkComboItem");
        foreach (var sec in Intervals)
            CmbInterval.Items.Add(new ComboBoxItem
            {
                Content = sec < 60 ? $"{sec}s" : $"{sec / 60}m {sec % 60:00}s",
                Tag     = sec,
                Style   = darkItemStyle
            });
        CmbInterval.SelectedIndex = 1; // default 10 s

        // Wire timer (ticks every second for the countdown display)
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick    += Timer_Tick;

        // Stop timer when page is unloaded (navigation away)
        Unloaded += (_, _) => StopTimer();
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        _allNodes = (await _repo.GetProcessesAsync()).ToList();
        RenderCanvas(_allNodes);
    }

    // ?? Timer logic ???????????????????????????????????????????????????
    private void StartTimer()
    {
        _remainingSec = _intervalSec;
        UpdateCountdown();
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer.Stop();
        TxtCountdown.Text = "--";
        if (BtnAutoRefresh.IsChecked == true)
            BtnAutoRefresh.IsChecked = false;
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        _remainingSec--;
        UpdateCountdown();

        if (_remainingSec > 0 || _refreshing) return;

        _refreshing = true;
        try
        {
            _allNodes = (await _repo.GetProcessesAsync()).ToList();
            RenderCanvas(_allNodes);
        }
        catch { /* silently skip failed ticks */ }
        finally
        {
            _refreshing   = false;
            _remainingSec = _intervalSec;   // reset countdown
            UpdateCountdown();
        }
    }

    private void UpdateCountdown()
    {
        TxtCountdown.Text = _remainingSec >= 60
            ? $"{_remainingSec / 60}:{_remainingSec % 60:00}"
            : $"{_remainingSec}s";
    }

    // ?? UI event handlers ?????????????????????????????????????????????
    private void CmbInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbInterval.SelectedItem is ComboBoxItem item && item.Tag is int sec)
        {
            _intervalSec  = sec;
            _remainingSec = sec;
            UpdateCountdown();
        }
    }

    private void BtnAutoRefresh_Checked(object sender, RoutedEventArgs e)
    {
        BtnAutoRefresh.Content = "■ Stop";
        BtnAutoRefresh.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x40));
        BtnAutoRefresh.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xA0, 0x40));
        BtnAutoRefresh.Background  = new SolidColorBrush(Color.FromArgb(0x22, 0xC0, 0x60, 0x00));
        StartTimer();
    }

    private void BtnAutoRefresh_Unchecked(object sender, RoutedEventArgs e)
    {
        BtnAutoRefresh.Content = "▶ Start";
        BtnAutoRefresh.Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0xDD, 0x66));
        BtnAutoRefresh.BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0xAA, 0x44));
        BtnAutoRefresh.Background  = new SolidColorBrush(Color.FromArgb(0xFF, 0x1E, 0x3A, 0x1E));
        _timer.Stop();
        TxtCountdown.Text = "--";
    }

    // ?? Search filter ??????????????????????????????????????????????????
    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchText = TxtSearch.Text.Trim();
        RenderCanvas(_allNodes);
    }

    private void BtnLegend_Click(object sender, RoutedEventArgs e)
    {
        LegendPanel.Visibility = LegendPanel.Visibility == Visibility.Visible
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    // ?? Canvas rendering ???????????????????????????????????????????????
    private void RenderCanvas(IReadOnlyList<ProcessNode> nodes)
    {
        ProcessCanvas.Children.Clear();

        // Filter
        var visible = string.IsNullOrEmpty(_searchText)
            ? nodes
            : nodes.Where(n => n.SessionId.ToString().Contains(_searchText)).ToList();

        if (visible.Count == 0) return;

        // Layout: give every node a position
        // Arrange in a force-like grid, blocking trees together
        var positions = ComputeLayout(visible);

        double maxX = positions.Values.Max(p => p.X) + BallDiam + 20;
        double maxY = positions.Values.Max(p => p.Y) + BallDiam + 20;
        ProcessCanvas.Width  = Math.Max(maxX, ProcessCanvas.MinWidth);
        ProcessCanvas.Height = Math.Max(maxY, ProcessCanvas.MinHeight);

        // Draw edges first (so balls render on top)
        var idToPos = positions;
        foreach (var node in visible.Where(n => n.ParentId != 0))
        {
            if (!idToPos.TryGetValue(node.ParentId,  out var parentPt)) continue;
            if (!idToPos.TryGetValue(node.SessionId, out var childPt))  continue;

            var line = new Line
            {
                X1 = parentPt.X + BallRadius,
                Y1 = parentPt.Y + BallRadius,
                X2 = childPt.X  + BallRadius,
                Y2 = childPt.Y  + BallRadius,
                Stroke = new SolidColorBrush(Color.FromArgb(180, 255, 80, 80)),
                StrokeThickness = 1.8,
                StrokeDashArray = new DoubleCollection { 4, 3 }
            };
            ProcessCanvas.Children.Add(line);
        }

        // Draw balls
        foreach (var node in visible)
        {
            var pt = positions[node.SessionId];
            DrawBall(node, pt.X, pt.Y);
        }
    }

    // ?? Layout algorithm ???????????????????????????????????????????????
    // Groups sessions into blocking chains; places root nodes in a grid,
    // blocked children in arcs below their blocker.
    private static Dictionary<int, Point> ComputeLayout(IEnumerable<ProcessNode> nodes)
    {
        var list    = nodes.ToList();
        var result  = new Dictionary<int, Point>();
        var spacing = BallDiam + 28;

        // Build adjacency: parent ? children
        var children = new Dictionary<int, List<int>>();
        foreach (var n in list)
        {
            if (n.ParentId != 0)
            {
                if (!children.ContainsKey(n.ParentId)) children[n.ParentId] = [];
                children[n.ParentId].Add(n.SessionId);
            }
        }

        // Roots = nodes with no blocker or whose blocker is outside the visible set
        var visibleIds = new HashSet<int>(list.Select(n => n.SessionId));
        var roots = list.Where(n => n.ParentId == 0 || !visibleIds.Contains(n.ParentId)).ToList();

        double curX = 20;
        double curY = 20;
        int col     = 0;
        const int cols = 8;

        void PlaceSubtree(int id, double rootX, double rootY, int depth)
        {
            if (result.ContainsKey(id)) return;
            result[id] = new Point(rootX, rootY);

            if (!children.TryGetValue(id, out var kids)) return;

            double childY  = rootY + spacing * 1.6;
            double totalW  = kids.Count * spacing;
            double startX  = rootX - (totalW - spacing) / 2.0;

            for (int i = 0; i < kids.Count; i++)
                PlaceSubtree(kids[i], startX + i * spacing, childY, depth + 1);
        }

        foreach (var root in roots)
        {
            double x = 20 + (col % cols) * spacing * 1.8;
            double y = curY + Math.Floor(col / (double)cols) * spacing * 3.2;
            PlaceSubtree(root.SessionId, x, y, 0);
            col++;
        }

        // Any orphaned node not yet placed
        foreach (var n in list.Where(n => !result.ContainsKey(n.SessionId)))
            result[n.SessionId] = new Point(curX + col++ * spacing, 20);

        // Normalise so nothing is clipped
        double minX = result.Values.Min(p => p.X);
        double minY = result.Values.Min(p => p.Y);
        if (minX < 20 || minY < 20)
        {
            double dx = Math.Max(0, 20 - minX);
            double dy = Math.Max(0, 20 - minY);
            var keys = result.Keys.ToList();
            foreach (var k in keys) result[k] = new Point(result[k].X + dx, result[k].Y + dy);
        }

        return result;
    }

    // ?? Draw a single ball ?????????????????????????????????????????????
    private void DrawBall(ProcessNode node, double x, double y)
    {
        var (fillBrush, strokeColor) = BallStyle(node.VisualState);

        var ball = new Ellipse
        {
            Width  = BallDiam,
            Height = BallDiam,
            Fill   = fillBrush,
            Stroke = new SolidColorBrush(strokeColor),
            StrokeThickness = node.VisualState == "HeadBlocker" ? 2.5 : 1.5,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color     = strokeColor,
                BlurRadius = 8,
                ShadowDepth = 0,
                Opacity   = 0.6
            }
        };
        Canvas.SetLeft(ball, x);
        Canvas.SetTop(ball,  y);
        ball.ToolTip = BuildTooltip(node);
        ball.Cursor  = Cursors.Hand;
        ball.Tag     = node;
        ball.MouseLeftButtonDown += Ball_MouseLeftButtonDown;
        ProcessCanvas.Children.Add(ball);

        // Session ID label
        var lbl = new TextBlock
        {
            Text       = node.SessionId.ToString(),
            Foreground = Brushes.White,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 10,
            FontWeight = FontWeights.Bold,
            IsHitTestVisible = false
        };
        lbl.Measure(new Size(BallDiam, BallDiam));
        double lx = x + (BallDiam - lbl.DesiredSize.Width)  / 2;
        double ly = y + (BallDiam - lbl.DesiredSize.Height) / 2;
        Canvas.SetLeft(lbl, lx);
        Canvas.SetTop(lbl,  ly);
        ProcessCanvas.Children.Add(lbl);
    }

    // ?? Color scheme per visual state ?????????????????????????????????
    private static (Brush fill, Color stroke) BallStyle(string state) => state switch
    {
        "Active" => (
            new RadialGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromRgb(0xFF, 0x80, 0x80), 0.0),
                    new(Color.FromRgb(0xCC, 0x00, 0x00), 1.0)
                })
            { GradientOrigin = new Point(0.35, 0.35) },
            Color.FromRgb(0xFF, 0x40, 0x40)),

        "HeadBlocker" => (
            new RadialGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromRgb(0xFF, 0xA0, 0x40), 0.0),
                    new(Color.FromRgb(0xC0, 0x40, 0x00), 1.0)
                })
            { GradientOrigin = new Point(0.35, 0.35) },
            Color.FromRgb(0xFF, 0x80, 0x00)),

        "Blocked" => (
            new RadialGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromRgb(0x90, 0x90, 0xFF), 0.0),
                    new(Color.FromRgb(0x00, 0x00, 0xCC), 1.0)
                })
            { GradientOrigin = new Point(0.35, 0.35) },
            Color.FromRgb(0x60, 0x60, 0xFF)),

        _ => (
            new RadialGradientBrush(
                new GradientStopCollection
                {
                    new(Color.FromRgb(0x60, 0xAA, 0xFF), 0.0),
                    new(Color.FromRgb(0x22, 0x44, 0xAA), 1.0)
                })
            { GradientOrigin = new Point(0.35, 0.35) },
            Color.FromRgb(0x44, 0x88, 0xFF))
    };

    // ?? Tooltip content ????????????????????????????????????????????????
    private static ToolTip BuildTooltip(ProcessNode node)
    {
        string waitInfo = node.WaitType is not null
            ? $"\nWait: {node.WaitType} ({node.WaitTimeMs:N0} ms)"
            : "";
        string blocker = node.ParentId != 0
            ? $"\nBlocked by: SPID {node.ParentId}"
            : "";
        string cmd = string.IsNullOrWhiteSpace(node.CommandText)
            ? ""
            : $"\n{node.CommandText[..Math.Min(200, node.CommandText.Length)]}";

        var tip = new ToolTip
        {
            Background = new SolidColorBrush(Color.FromArgb(240, 20, 20, 40)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 120)),
            Foreground = Brushes.White
        };
        var sp = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };
        sp.Children.Add(new TextBlock
        {
            Text       = $"SPID {node.SessionId}  [{node.VisualState}]",
            FontWeight = FontWeights.Bold,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 12,
            Foreground = Brushes.White
        });
        sp.Children.Add(new TextBlock
        {
            Text       = $"User: {node.UserName ?? "-"}  Host: {node.HostName ?? "-"}" +
                         $"\nStatus: {node.Status}{blocker}{waitInfo}{cmd}",
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth   = 360
        });
        tip.Content = sp;
        return tip;
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e) { }

    // ?? Details Popup ?????????????????????????????????????????????????
    private void Ball_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Ellipse ball || ball.Tag is not ProcessNode node) return;

        // Build key-value detail text
        var sb = new System.Text.StringBuilder();
        void Add(string label, object? value) =>
            sb.AppendLine($"{label,-16} : {value}");

        Add("spid",          node.SessionId);
        Add("blocked",       node.ParentId);
        Add("status",        node.Status);
        Add("waittime",      node.WaitTimeMs);
        Add("waittype",      node.WaitType ?? "");
        Add("lastwaittype",  node.LastWaitType ?? "");
        Add("waitresource",  node.WaitResource ?? "");
        Add("database",      node.DatabaseName ?? "");
        Add("cpu",           node.CpuTime);
        Add("physical_io",   node.PhysicalIo);
        Add("memusage",      node.MemoryUsage);
        Add("login_time",    node.LoginTime);
        Add("last_batch",    node.LastBatch);
        Add("open_tran",     node.OpenTran);
        Add("program_name",  node.ProgramName ?? "");
        Add("host_name",     node.HostName ?? "");
        Add("loginname",     node.UserName ?? "");
        Add("cmd",           node.Command ?? "");

        TxtDetailsContent.Text = sb.ToString();
        TxtInfoBuffer.Text     = node.CommandText ?? "";
        DetailsPopup.Visibility = Visibility.Visible;
        e.Handled = true;
    }

    private void BtnCloseDetails_Click(object sender, RoutedEventArgs e) =>
        DetailsPopup.Visibility = Visibility.Collapsed;
}
