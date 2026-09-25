using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Controls
{
    public partial class ProcessMapControl : UserControl
    {
        private const double BallRadius = 22;
        private const double BallDiam   = BallRadius * 2;
        private IReadOnlyList<ProcessNode> _allNodes = [];
        private IReadOnlyList<BlockingChain> _chains = [];
        private readonly Dictionary<int, Ellipse> _balls = [];
        private string _procSearch = "";
        private bool   _chainsOnly;
        private double _zoomScale = 1.0;
        private const double ZoomStep = 0.1;
        private const double ZoomMin  = 0.2;
        private const double ZoomMax  = 3.0;
        private bool  _isDragging;
        private Point _dragStart;
        private Point _panelStart;

        public ProcessMapControl()
        {
            InitializeComponent();
        }

        public void UpdateNodes(IEnumerable<ProcessNode> nodes)
        {
            _allNodes = BlockingChains.Classify(nodes);
            _chains   = BlockingChains.Find(_allNodes);
            RenderChainSummary();
            RenderProcessCanvas(_allNodes);
        }

        // ── Head blockers: one chip per chain, the largest first ──────────
        private void RenderChainSummary()
        {
            HeadBlockerChips.Children.Clear();
            int blocked = _chains.Sum(c => c.BlockedCount);
            TxtChainSummary.Text = _chains.Count switch
            {
                0 => "No blocking",
                1 => $"1 blocking chain, {blocked} blocked. Head blocker:",
                _ => $"{_chains.Count} blocking chains, {blocked} blocked. Head blockers:",
            };

            foreach (var chain in _chains)
            {
                var chip = new Button
                {
                    Content         = $"SPID {chain.HeadSessionId}  ·  {chain.BlockedCount} blocked  ·  longest wait {FormatWait(chain.LongestWaitMs)}",
                    Tag             = chain.HeadSessionId,
                    Height          = 24,
                    Padding         = new Thickness(8, 0, 8, 0),
                    Margin          = new Thickness(0, 0, 6, 0),
                    Background      = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0x80, 0x00)),
                    BorderBrush     = new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0x00)),
                    BorderThickness = new Thickness(1),
                    FontFamily      = new FontFamily("Segoe UI"),
                    FontSize        = 11,
                    Cursor          = Cursors.Hand,
                    ToolTip         = "Show the head blocker and its session details",
                };
                chip.SetResourceReference(ForegroundProperty, "TextPrimary");
                chip.Click += (_, _) => GoToSession(chain.HeadSessionId);
                HeadBlockerChips.Children.Add(chip);
            }
        }

        private static string FormatWait(long ms) =>
            ms < 1_000  ? $"{ms} ms"
            : ms < 60_000 ? $"{ms / 1000.0:0.#} s"
            : $"{ms / 60_000.0:0.#} min";

        private void GoToSession(int sessionId)
        {
            // Bring it back if the search or the filter hides it.
            if (!_balls.ContainsKey(sessionId) && !string.IsNullOrEmpty(TxtProcSearch.Text))
                TxtProcSearch.Text = "";
            if (!_balls.TryGetValue(sessionId, out var ball) || ball.Tag is not ProcessNode node) return;

            ball.BringIntoView();
            ProcessScrollViewer.UpdateLayout();
            ShowDetails(node, ball.TranslatePoint(new Point(BallDiam, 0), OverlayCanvas));
        }

        private void RenderProcessCanvas(IReadOnlyList<ProcessNode> nodes)
        {
            ProcessCanvas.Children.Clear();
            _balls.Clear();

            var visible = nodes;
            if (_chainsOnly)
            {
                var inChains = BlockingChains.InChains(visible);
                visible = visible.Where(n => inChains.Contains(n.SessionId)).ToList();
            }
            if (!string.IsNullOrEmpty(_procSearch))
            {
                // Keep the whole chain of a matching session, so its head is on screen with it.
                var keep = new HashSet<int>();
                foreach (var match in visible.Where(n => n.SessionId.ToString().Contains(_procSearch)))
                    keep.UnionWith(BlockingChains.ChainOf(visible, match.SessionId));
                visible = visible.Where(n => keep.Contains(n.SessionId)).ToList();
            }

            if (visible.Count == 0) return;

            var positions = ComputeProcessLayout(visible);

            double maxX = positions.Values.Max(p => p.X) + BallDiam + 20;
            double maxY = positions.Values.Max(p => p.Y) + BallDiam + 34;
            ProcessCanvas.Width  = Math.Max(maxX, ProcessCanvas.MinWidth);
            ProcessCanvas.Height = Math.Max(maxY, ProcessCanvas.MinHeight);

            foreach (var node in visible.Where(n => n.ParentId != 0 && n.ParentId != n.SessionId))
            {
                if (!positions.TryGetValue(node.ParentId,  out var parentPt)) continue;
                if (!positions.TryGetValue(node.SessionId, out var childPt))  continue;

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

            foreach (var node in visible)
            {
                var pt = positions[node.SessionId];
                DrawProcessBall(node, pt.X, pt.Y);
            }
        }

        // Each chain is a tree with its head blocker at the top and every session under the one
        // blocking it; subtrees get their own columns, so branches never overlap. Chains come first,
        // the largest leftmost, wrapping onto a new band when a row is full. Sessions that aren't in
        // a chain follow in a grid below.
        private static Dictionary<int, Point> ComputeProcessLayout(IReadOnlyList<ProcessNode> nodes)
        {
            const double margin   = 20;
            const double slot     = BallDiam + 28;
            const double rowH     = slot * 1.6;
            const double maxWidth = slot * 16;
            const int    soloCols = 12;

            var forest = BlockingChains.Forest(nodes);
            var result = new Dictionary<int, Point>();

            var leaves = new Dictionary<int, int>();
            int Leaves(int id)
            {
                if (leaves.TryGetValue(id, out var n)) return n;
                var kids = forest.ChildrenOf(id);
                return leaves[id] = kids.Count == 0 ? 1 : kids.Sum(Leaves);
            }

            // Returns the subtree's depth below id.
            int Place(int id, double left, double top)
            {
                var kids = forest.ChildrenOf(id);
                if (kids.Count == 0)
                {
                    result[id] = new Point(left, top);
                    return 0;
                }

                int depth = 0;
                double childLeft = left;
                foreach (var kid in kids)
                {
                    depth = Math.Max(depth, 1 + Place(kid, childLeft, top + rowH));
                    childLeft += Leaves(kid) * slot;
                }
                result[id] = new Point((result[kids[0]].X + result[kids[^1]].X) / 2, top);
                return depth;
            }

            double x = margin, y = margin;
            int bandDepth = -1;   // deepest chain in the current band; -1 = no chain yet
            var solos = new List<int>();

            foreach (var root in forest.Roots)
            {
                if (forest.ChildrenOf(root).Count == 0) { solos.Add(root); continue; }

                double width = Leaves(root) * slot;
                if (bandDepth >= 0 && x + width > maxWidth)
                {
                    x = margin;
                    y += (bandDepth + 1) * rowH + slot * 0.4;
                    bandDepth = -1;
                }
                bandDepth = Math.Max(bandDepth, Place(root, x, y));
                x += width + slot * 0.6;
            }

            double soloTop = bandDepth >= 0 ? y + (bandDepth + 1) * rowH + slot * 0.4 : margin;
            for (int i = 0; i < solos.Count; i++)
                result[solos[i]] = new Point(margin + i % soloCols * slot, soloTop + i / soloCols * slot);

            return result;
        }

        private void DrawProcessBall(ProcessNode node, double x, double y)
        {
            var (fillBrush, strokeColor) = ProcessBallStyle(node.VisualState);

            var ball = new Ellipse
            {
                Width  = BallDiam,
                Height = BallDiam,
                Fill   = fillBrush,
                Stroke = new SolidColorBrush(strokeColor),
                StrokeThickness = node.VisualState == "HeadBlocker" ? 2.5 : 1.5,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color      = strokeColor,
                    BlurRadius  = 8,
                    ShadowDepth = 0,
                    Opacity    = 0.6
                }
            };
            Canvas.SetLeft(ball, x);
            Canvas.SetTop(ball,  y);
            ball.ToolTip = BuildProcessTooltip(node);
            ball.Cursor  = Cursors.Hand;
            ball.Tag     = node;
            ball.MouseLeftButtonDown += ProcBall_MouseLeftButtonDown;
            ProcessCanvas.Children.Add(ball);
            _balls[node.SessionId] = ball;

            if (node.VisualState == BlockingChains.HeadBlocker)
            {
                int blocked = _chains.FirstOrDefault(c => c.HeadSessionId == node.SessionId)?.BlockedCount ?? 0;
                var head = new TextBlock
                {
                    Text       = blocked > 0 ? $"HEAD · {blocked}" : "HEAD",
                    Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0x00)),
                    FontFamily = new FontFamily("Segoe UI"),
                    FontSize   = 10,
                    FontWeight = FontWeights.Bold,
                    IsHitTestVisible = false
                };
                head.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Canvas.SetLeft(head, x + (BallDiam - head.DesiredSize.Width) / 2);
                Canvas.SetTop(head,  y + BallDiam + 2);
                ProcessCanvas.Children.Add(head);
            }

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
            Canvas.SetLeft(lbl, x + (BallDiam - lbl.DesiredSize.Width)  / 2);
            Canvas.SetTop(lbl,  y + (BallDiam - lbl.DesiredSize.Height) / 2);
            ProcessCanvas.Children.Add(lbl);
        }

        private static (Brush fill, Color stroke) ProcessBallStyle(string state) => state switch
        {
            "Active" => (
                new RadialGradientBrush(new GradientStopCollection
                {
                    new(Color.FromRgb(0xFF, 0x80, 0x80), 0.0),
                    new(Color.FromRgb(0xCC, 0x00, 0x00), 1.0)
                }) { GradientOrigin = new Point(0.35, 0.35) },
                Color.FromRgb(0xFF, 0x40, 0x40)),

            "HeadBlocker" => (
                new RadialGradientBrush(new GradientStopCollection
                {
                    new(Color.FromRgb(0xFF, 0xA0, 0x40), 0.0),
                    new(Color.FromRgb(0xC0, 0x40, 0x00), 1.0)
                }) { GradientOrigin = new Point(0.35, 0.35) },
                Color.FromRgb(0xFF, 0x80, 0x00)),

            "Blocked" => (
                new RadialGradientBrush(new GradientStopCollection
                {
                    new(Color.FromRgb(0x90, 0x90, 0xFF), 0.0),
                    new(Color.FromRgb(0x00, 0x00, 0xCC), 1.0)
                }) { GradientOrigin = new Point(0.35, 0.35) },
                Color.FromRgb(0x60, 0x60, 0xFF)),

            _ => (
                new RadialGradientBrush(new GradientStopCollection
                {
                    new(Color.FromRgb(0x60, 0xAA, 0xFF), 0.0),
                    new(Color.FromRgb(0x22, 0x44, 0xAA), 1.0)
                }) { GradientOrigin = new Point(0.35, 0.35) },
                Color.FromRgb(0x44, 0x88, 0xFF))
        };

        private ToolTip BuildProcessTooltip(ProcessNode node)
        {
            string waitInfo = node.WaitType is not null ? $"\nWait: {node.WaitType} ({node.WaitTimeMs:N0} ms)" : "";
            string blocker  = node.ParentId != 0 ? $"\nBlocked by: {BlockingChains.DescribeBlocker(node.ParentId)}" : "";
            var    chain    = _chains.FirstOrDefault(c => c.HeadSessionId == node.SessionId);
            string head     = chain is not null ? $"\nHead blocker: {chain.BlockedCount} session(s) waiting behind it" : "";
            string cmd      = string.IsNullOrWhiteSpace(node.CommandText) ? ""
                : $"\n{node.CommandText[..Math.Min(200, node.CommandText.Length)]}";

            var tip = new ToolTip
            {
                Background  = new SolidColorBrush(Color.FromArgb(240, 20, 20, 40)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(80, 80, 120)),
                Foreground  = Brushes.White
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
                Text         = $"Login: {node.UserName ?? "-"}  Host: {node.HostName ?? "-"}" +
                               $"\nProgram: {node.ProgramName ?? "-"}" +
                               $"\nStatus: {node.Status}  Open tran: {node.OpenTran}{head}{blocker}{waitInfo}{cmd}",
                FontFamily   = new FontFamily("Segoe UI"),
                FontSize     = 11,
                Foreground   = new SolidColorBrush(Color.FromRgb(200, 200, 200)),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth     = 360
            });
            tip.Content = sp;
            return tip;
        }

        private void ApplyZoom()
        {
            CanvasScale.ScaleX = _zoomScale;
            CanvasScale.ScaleY = _zoomScale;
            TxtZoomLevel.Text  = $"{(int)Math.Round(_zoomScale * 100)}%";
        }

        private void BtnZoomIn_Click(object sender, RoutedEventArgs e)
        {
            _zoomScale = Math.Min(ZoomMax, _zoomScale + ZoomStep);
            ApplyZoom();
        }

        private void BtnZoomOut_Click(object sender, RoutedEventArgs e)
        {
            _zoomScale = Math.Max(ZoomMin, _zoomScale - ZoomStep);
            ApplyZoom();
        }

        private void BtnZoomReset_Click(object sender, RoutedEventArgs e)
        {
            _zoomScale = 1.0;
            ApplyZoom();
        }

        private void ProcessScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                _zoomScale = e.Delta > 0
                    ? Math.Min(ZoomMax, _zoomScale + ZoomStep)
                    : Math.Max(ZoomMin, _zoomScale - ZoomStep);
                ApplyZoom();
                e.Handled = true;
            }
        }

        private void ProcCanvas_MouseMove(object sender, MouseEventArgs e) { }

        private void ProcBall_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Ellipse ball || ball.Tag is not ProcessNode node) return;

            ShowDetails(node, e.GetPosition(OverlayCanvas));
            e.Handled = true;
        }

        private void ShowDetails(ProcessNode node, Point near)
        {
            var sb = new System.Text.StringBuilder();
            void Add(string label, object? value) => sb.AppendLine($"{label,-16} : {value}");

            int head     = BlockingChains.HeadOf(_allNodes, node.SessionId);
            int blocking = _allNodes.Count(n => n.ParentId == node.SessionId && n.SessionId != node.SessionId);

            Add("spid",         node.SessionId);
            Add("blocked",      node.ParentId);
            if (node.ParentId != 0)
                Add("blocked_by", BlockingChains.DescribeBlocker(node.ParentId));
            if (head != node.SessionId || blocking > 0)
                Add("head_blocker", head);
            if (blocking > 0)
                Add("blocking",   $"{blocking} session(s) directly");
            Add("status",       node.Status);
            Add("waittime",     node.WaitTimeMs);
            Add("waittype",     node.WaitType ?? "");
            Add("lastwaittype", node.LastWaitType ?? "");
            Add("waitresource", node.WaitResource ?? "");
            Add("database",     node.DatabaseName ?? "");
            Add("cpu",          node.CpuTime);
            Add("physical_io",  node.PhysicalIo);
            Add("memusage",     node.MemoryUsage);
            Add("login_time",   node.LoginTime);
            Add("last_batch",   node.LastBatch);
            Add("open_tran",    node.OpenTran);
            Add("program_name", node.ProgramName ?? "");
            Add("host_name",    node.HostName ?? "");
            Add("loginname",    node.UserName ?? "");
            Add("cmd",          node.Command ?? "");

            TxtProcDetailsContent.Text  = sb.ToString();
            TxtProcInfoBuffer.Text       = node.CommandText ?? "";
            ProcDetailsPopup.Visibility  = Visibility.Visible;
            ProcDetailsPopup.UpdateLayout();

            // Position panel near the ball, clamped so it stays fully visible
            double panelW = ProcDetailsPopup.ActualWidth  > 0 ? ProcDetailsPopup.ActualWidth  : 380;
            double panelH = ProcDetailsPopup.ActualHeight > 0 ? ProcDetailsPopup.ActualHeight : 420;
            double left   = Math.Min(near.X + 20, OverlayCanvas.ActualWidth  - panelW - 10);
            double top    = Math.Min(near.Y - 30, OverlayCanvas.ActualHeight - panelH - 10);
            Canvas.SetLeft(ProcDetailsPopup, Math.Max(10, left));
            Canvas.SetTop(ProcDetailsPopup,  Math.Max(10, top));
        }

        private void BtnCloseProcDetails_Click(object sender, RoutedEventArgs e) =>
            ProcDetailsPopup.Visibility = Visibility.Collapsed;

        private void BtnCopySql_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(TxtProcInfoBuffer.Text))
                ClipboardHelper.SetText(TxtProcInfoBuffer.Text);
        }

        private void ProcDetailsDrag_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _isDragging = true;
            _dragStart  = e.GetPosition(OverlayCanvas);
            _panelStart = new Point(Canvas.GetLeft(ProcDetailsPopup), Canvas.GetTop(ProcDetailsPopup));
            ((UIElement)sender).CaptureMouse();
            e.Handled = true;
        }

        private void ProcDetailsDrag_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDragging) return;
            var pos      = e.GetPosition(OverlayCanvas);
            double left  = _panelStart.X + (pos.X - _dragStart.X);
            double top   = _panelStart.Y + (pos.Y - _dragStart.Y);
            left = Math.Max(0, Math.Min(OverlayCanvas.ActualWidth  - ProcDetailsPopup.ActualWidth,  left));
            top  = Math.Max(0, Math.Min(OverlayCanvas.ActualHeight - ProcDetailsPopup.ActualHeight, top));
            Canvas.SetLeft(ProcDetailsPopup, left);
            Canvas.SetTop(ProcDetailsPopup,  top);
        }

        private void ProcDetailsDrag_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
            ((UIElement)sender).ReleaseMouseCapture();
        }

        private void TxtProcInfoBuffer_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)
            {
                TxtProcInfoBuffer.SelectAll();
                e.Handled = true;
            }
        }

        private void TxtProcSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _procSearch = TxtProcSearch.Text.Trim();
            RenderProcessCanvas(_allNodes);
        }

        private void ChkChainsOnly_Changed(object sender, RoutedEventArgs e)
        {
            _chainsOnly = ChkChainsOnly.IsChecked == true;
            RenderProcessCanvas(_allNodes);
        }

        private void BtnProcLegend_Click(object sender, RoutedEventArgs e)
        {
            ProcLegendPanel.Visibility = ProcLegendPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
