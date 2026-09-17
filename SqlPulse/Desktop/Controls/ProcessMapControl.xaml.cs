using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SqlPulse.Desktop.Helpers;
using SqlPulse.Engine.Models;

namespace SqlPulse.Desktop.Controls
{
    public partial class ProcessMapControl : UserControl
    {
        private const double BallRadius = 22;
        private const double BallDiam   = BallRadius * 2;
        private List<ProcessNode> _allNodes = [];
        private string _procSearch = "";
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
            _allNodes = nodes.ToList();
            RenderProcessCanvas(_allNodes);
        }

        private void RenderProcessCanvas(IReadOnlyList<ProcessNode> nodes)
        {
            ProcessCanvas.Children.Clear();

            var visible = string.IsNullOrEmpty(_procSearch)
                ? nodes
                : nodes.Where(n => n.SessionId.ToString().Contains(_procSearch)).ToList();

            if (visible.Count == 0) return;

            var positions = ComputeProcessLayout(visible);

            double maxX = positions.Values.Max(p => p.X) + BallDiam + 20;
            double maxY = positions.Values.Max(p => p.Y) + BallDiam + 20;
            ProcessCanvas.Width  = Math.Max(maxX, ProcessCanvas.MinWidth);
            ProcessCanvas.Height = Math.Max(maxY, ProcessCanvas.MinHeight);

            foreach (var node in visible.Where(n => n.ParentId != 0))
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

        private static Dictionary<int, Point> ComputeProcessLayout(IEnumerable<ProcessNode> nodes)
        {
            var list    = nodes.ToList();
            var result  = new Dictionary<int, Point>();
            var spacing = BallDiam + 28;

            var children = new Dictionary<int, List<int>>();
            foreach (var n in list)
            {
                if (n.ParentId != 0)
                {
                    if (!children.ContainsKey(n.ParentId)) children[n.ParentId] = [];
                    children[n.ParentId].Add(n.SessionId);
                }
            }

            var visibleIds = new HashSet<int>(list.Select(n => n.SessionId));
            var roots = list.Where(n => n.ParentId == 0 || !visibleIds.Contains(n.ParentId)).ToList();

            double curX = 20;
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

            double curY = 20;
            foreach (var root in roots)
            {
                double x = 20 + (col % cols) * spacing * 1.8;
                double y = curY + Math.Floor(col / (double)cols) * spacing * 3.2;
                PlaceSubtree(root.SessionId, x, y, 0);
                col++;
            }

            foreach (var n in list.Where(n => !result.ContainsKey(n.SessionId)))
                result[n.SessionId] = new Point(curX + col++ * spacing, 20);

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

        private static ToolTip BuildProcessTooltip(ProcessNode node)
        {
            string waitInfo = node.WaitType is not null ? $"\nWait: {node.WaitType} ({node.WaitTimeMs:N0} ms)" : "";
            string blocker  = node.ParentId != 0 ? $"\nBlocked by: SPID {node.ParentId}" : "";
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
                Text         = $"User: {node.UserName ?? "-"}  Host: {node.HostName ?? "-"}" +
                               $"\nStatus: {node.Status}{blocker}{waitInfo}{cmd}",
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

            var sb = new System.Text.StringBuilder();
            void Add(string label, object? value) => sb.AppendLine($"{label,-16} : {value}");

            Add("spid",         node.SessionId);
            Add("blocked",      node.ParentId);
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

            // Position panel near the clicked ball, clamped so it stays fully visible
            var clickPos = e.GetPosition(OverlayCanvas);
            double panelW = ProcDetailsPopup.ActualWidth  > 0 ? ProcDetailsPopup.ActualWidth  : 380;
            double panelH = ProcDetailsPopup.ActualHeight > 0 ? ProcDetailsPopup.ActualHeight : 420;
            double left   = Math.Min(clickPos.X + 20, OverlayCanvas.ActualWidth  - panelW - 10);
            double top    = Math.Min(clickPos.Y - 30, OverlayCanvas.ActualHeight - panelH - 10);
            Canvas.SetLeft(ProcDetailsPopup, Math.Max(10, left));
            Canvas.SetTop(ProcDetailsPopup,  Math.Max(10, top));

            e.Handled = true;
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

        private void BtnProcLegend_Click(object sender, RoutedEventArgs e)
        {
            ProcLegendPanel.Visibility = ProcLegendPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
