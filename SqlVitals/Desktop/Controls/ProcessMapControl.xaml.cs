using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;

// Block-scoped on purpose for now: converting re-indents the whole file. Convert it in a change that
// rewrites the file anyway.
#pragma warning disable IDE0161
namespace SqlVitals.Desktop.Controls
#pragma warning restore IDE0161
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

        // ── Grid view (#116) ──────────────────────────────────────────────
        private bool _showGrid;
        private bool _mapStale, _gridStale;   // the snapshot changed while the other view was shown
        private readonly List<ProcessGridRow> _gridRows = [];
        private readonly ListCollectionView _gridView;
        private readonly PropertyGroupDescription _byLogin =
            new(nameof(ProcessGridRow.Group)) { CustomSort = ProcessGroupHeader.GroupOrder.Instance };
        private readonly Dictionary<string, ProcessGroupHeader> _groupHeaders = [];
        private readonly HashSet<string> _hiddenStates = [];
        private bool _restoringSelection;
        private ScrollViewer? _gridScroll;

        /// <summary>Raised when the user ticks or unticks Group by login, so the page can remember it.</summary>
        public event EventHandler? GroupByLoginChanged;

        // ── A blocking alert's evidence (#145) ────────────────────────────
        // While on, only one chain is shown: its head pinned once found, so a refresh doesn't jump
        // to another chain while this one lasts.
        private bool _chainFocus;
        private int? _focusHead;
        private bool _hasSnapshot;

        // The session whose details the drawer shows (#147), kept across refreshes; null for none.
        private int? _selected;

        /// <summary>The chain shown changed, or the snapshot did: the page's banner says what is shown.</summary>
        public event EventHandler? FocusChanged;

        /// <summary>
        /// The selected session, for the details drawer (#147): the session as the latest snapshot
        /// has it, or null when it has gone since. <c>true</c> when the user clicked it.
        /// </summary>
        public event Action<int, ProcessNode?, bool>? SessionSelected;

        /// <summary>The session selected on the map or in the grid; null for none.</summary>
        public int? SelectedSession => _selected;

        // Selects a session for the drawer, and rings it on the map.
        private void Select(ProcessNode node, bool open)
        {
            var changed = _selected != node.SessionId;
            _selected = node.SessionId;
            if (changed && !_showGrid)
                MarkSelected();
            SessionSelected?.Invoke(node.SessionId, node, open);
        }

        /// <summary>
        /// Shows only one blocking chain: the one <paramref name="head"/> heads, or with null the
        /// chain whose blocked session has waited longest. Applies to the snapshot shown, if any,
        /// and to every one after it.
        /// </summary>
        public void FocusChain(int? head)
        {
            _chainFocus = true;
            _focusHead  = head;
            if (_hasSnapshot)
            {
                _focusHead = BlockingChains.EvidenceHead(_chains, _focusHead);
                Render();
            }
            FocusChanged?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>Every session again: Show all.</summary>
        public void ClearChainFocus()
        {
            if (!_chainFocus) return;
            _chainFocus = false;
            _focusHead  = null;
            Render();
            FocusChanged?.Invoke(this, EventArgs.Empty);
        }

        public bool IsChainFocused => _chainFocus;

        /// <summary>The head of the chain shown, while focused; null when nothing is blocked or before the first snapshot.</summary>
        public int? FocusedHead => _chainFocus ? _focusHead : null;

        /// <summary>A snapshot has been drawn.</summary>
        public bool HasSnapshot => _hasSnapshot;

        /// <summary>Every session in the last snapshot.</summary>
        public int SessionCount => _allNodes.Count;

        /// <summary>The last snapshot, classified, for the page's verdict (#146).</summary>
        public IReadOnlyList<ProcessNode> Snapshot => _allNodes;

        /// <summary>Selects a session and shows its details, as its head blocker chip does: the verdict's "Go to session".</summary>
        public void ShowSession(int sessionId) => GoToSession(sessionId);

        /// <summary>Ticks Blocking chains only: the verdict's action.</summary>
        public void ShowChainsOnly() => ChkChainsOnly.IsChecked = true;

        public ProcessMapControl()
        {
            InitializeComponent();

            // One view for the control's lifetime: a refresh swaps the rows under it and refreshes
            // it, so the sort, the grouping and the filter stay as the user left them.
            _gridView = new ListCollectionView(_gridRows);
            _gridView.GroupDescriptions.Add(_byLogin);
            ProcessGrid.ItemsSource = _gridView;
            GridFilter.ExtraRowText = row => (row as ProcessGridRow)?.Login;

            // Ctrl+C copies the selected rows with their headers, as "Copy with headers" does.
            ProcessGrid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
                (_, e) => { DataGridExport.Copy(ProcessGrid, includeHeaders: true); e.Handled = true; },
                (_, e) => { e.CanExecute = _gridView.Count > 0; e.Handled = true; }));

            // The grid handles the mouse itself, so the SPID links need to see clicks it has handled too.
            ProcessGrid.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(ProcessGrid_MouseLeftButtonUp), handledEventsToo: true);

            UpdateStateChips([]);
        }

        /// <summary>
        /// The snapshot to draw, bound from the page's view model (#186), already classified
        /// (<see cref="BlockingChains.Classify"/>): each new one is drawn as <see cref="UpdateNodes"/>
        /// draws it. Null draws nothing.
        /// </summary>
        public IReadOnlyList<ProcessNode>? Sessions
        {
            get => (IReadOnlyList<ProcessNode>?)GetValue(SessionsProperty);
            set => SetValue(SessionsProperty, value);
        }

        public static readonly DependencyProperty SessionsProperty =
            DependencyProperty.Register(nameof(Sessions), typeof(IReadOnlyList<ProcessNode>), typeof(ProcessMapControl),
                new PropertyMetadata(null, (d, e) =>
                {
                    if (e.NewValue is IReadOnlyList<ProcessNode> nodes)
                        ((ProcessMapControl)d).Draw(nodes);
                }));

        public void UpdateNodes(IEnumerable<ProcessNode> nodes) => Draw(BlockingChains.Classify(nodes));

        // A snapshot classified into chains.
        private void Draw(IReadOnlyList<ProcessNode> classified)
        {
            _allNodes    = classified;
            _chains      = BlockingChains.Find(_allNodes);
            _hasSnapshot = true;
            if (_chainFocus)
                _focusHead = BlockingChains.EvidenceHead(_chains, _focusHead);
            RenderChainSummary();
            Render();
            if (_chainFocus)
                FocusChanged?.Invoke(this, EventArgs.Empty);

            // The selected session as it is now, or gone.
            if (_selected is { } id)
                SessionSelected?.Invoke(id, _allNodes.FirstOrDefault(n => n.SessionId == id), false);
        }

        /// <summary>The grid rather than the map (#116). Both show the same snapshot: switching doesn't query the server.</summary>
        public bool ShowGrid
        {
            get => _showGrid;
            set
            {
                if (_showGrid == value) return;
                _showGrid = value;

                ProcessGrid.Visibility         = value ? Visibility.Visible   : Visibility.Collapsed;
                GridControls.Visibility        = value ? Visibility.Visible   : Visibility.Collapsed;
                GridFilters.Visibility         = value ? Visibility.Visible   : Visibility.Collapsed;
                ProcessScrollViewer.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
                ZoomControls.Visibility        = value ? Visibility.Collapsed : Visibility.Visible;

                if (value ? _gridStale : _mapStale) Render();
            }
        }

        /// <summary>The grid is grouped by login; off, it is one flat list with a Login column.</summary>
        public bool GroupByLogin
        {
            get => ChkGroupByLogin.IsChecked == true;
            set => ChkGroupByLogin.IsChecked = value;
        }

        // Draws the view on screen; the other catches up when it is switched to.
        private void Render()
        {
            var visible = BlockingChains.Visible(_allNodes, _chainsOnly, _procSearch);
            if (_chainFocus)
            {
                // The chain as the whole snapshot has it, whatever else hides some of its sessions.
                var chain = _focusHead is { } head ? BlockingChains.ChainOf(_allNodes, head) : new HashSet<int>();
                visible = visible.Where(n => chain.Contains(n.SessionId)).ToList();
            }
            if (_showGrid)
            {
                RenderGrid(visible);
                _mapStale = true;
            }
            else
            {
                RenderProcessCanvas(visible);
                _gridStale = true;
            }
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
                    Background      = ThemeResources.Wash(ProcessStates.ColorOf(BlockingChains.HeadBlocker), 0x26),
                    BorderBrush     = ProcessStates.BrushOf(BlockingChains.HeadBlocker),
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
            // Another chain's head chip while one chain is shown: show that chain instead.
            if (_chainFocus && (_focusHead is not { } head || !BlockingChains.ChainOf(_allNodes, head).Contains(sessionId)))
            {
                _focusHead = BlockingChains.HeadOf(_allNodes, sessionId);
                Render();
                FocusChanged?.Invoke(this, EventArgs.Empty);
            }

            if (_showGrid)
            {
                GoToRow(sessionId);
                return;
            }

            // Bring it back if the search or the filter hides it.
            if (!_balls.ContainsKey(sessionId) && !string.IsNullOrEmpty(TxtProcSearch.Text))
                TxtProcSearch.Text = "";
            if (!_balls.TryGetValue(sessionId, out var ball) || ball.Tag is not ProcessNode node) return;

            ball.BringIntoView();
            Select(node, open: true);
        }

        private void RenderProcessCanvas(IReadOnlyList<ProcessNode> visible)
        {
            ProcessCanvas.Children.Clear();
            _balls.Clear();
            _mapStale = false;

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
                    Stroke = ThemeResources.Wash(ProcessStates.ColorOf(BlockingChains.Active), 180),
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

        // ── Grid view (#116) ──────────────────────────────────────────────

        private void RenderGrid(IReadOnlyList<ProcessNode> visible)
        {
            _gridStale = false;

            // What the user is looking at, to put back after the rows are replaced.
            _gridScroll ??= FindDescendant<ScrollViewer>(ProcessGrid);
            double vOffset = _gridScroll?.VerticalOffset ?? 0, hOffset = _gridScroll?.HorizontalOffset ?? 0;
            var selected = ProcessGrid.SelectedItems.Cast<ProcessGridRow>().Select(r => r.SessionId).ToHashSet();
            int? current = (ProcessGrid.SelectedItem as ProcessGridRow)?.SessionId;

            UpdateStateChips(visible);

            // Heads and "blocking" counts come from the whole snapshot: a filter mustn't change them.
            var heads    = BlockingChains.Heads(_allNodes);
            var blocking = BlockingChains.BlockedDirectly(_allNodes);
            var rows = visible
                .Where(n => !_hiddenStates.Contains(n.VisualState))
                .OrderBy(n => n.SessionId)
                .Select(n => new ProcessGridRow(n, HeaderFor(n.UserName),
                    heads.TryGetValue(n.SessionId, out var head) ? head : n.SessionId,
                    blocking.GetValueOrDefault(n.SessionId),
                    heads.ContainsKey(n.ParentId)))
                .ToList();

            // A group whose sessions have all gone disappears, and starts expanded if it comes back.
            var live = rows.Select(r => r.Group.Key).ToHashSet();
            foreach (var key in _groupHeaders.Keys.Where(k => !live.Contains(k)).ToList())
                _groupHeaders.Remove(key);

            _restoringSelection = true;
            try
            {
                _gridRows.Clear();
                _gridRows.AddRange(rows);
                UpdateGroupHeaders();
                _gridView.Refresh();

                // Only rows the filter box left; a set, as every row may be selected (Ctrl+A).
                var shown = _gridView.Cast<ProcessGridRow>().ToHashSet();
                ProcessGrid.SelectedItems.Clear();
                foreach (var row in rows.Where(r => selected.Contains(r.SessionId) && shown.Contains(r)))
                    ProcessGrid.SelectedItems.Add(row);
                if (current is { } id && rows.FirstOrDefault(r => r.SessionId == id) is { } currentRow && shown.Contains(currentRow))
                    ProcessGrid.CurrentItem = currentRow;
            }
            finally
            {
                _restoringSelection = false;
            }

            // Once the rows are laid out again.
            if (_gridScroll is { } scroll)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
                {
                    scroll.ScrollToVerticalOffset(vOffset);
                    scroll.ScrollToHorizontalOffset(hOffset);
                });
        }

        private ProcessGroupHeader HeaderFor(string? login)
        {
            var key = ProcessGroups.KeyOf(login);
            if (!_groupHeaders.TryGetValue(key, out var header))
                _groupHeaders[key] = header = new ProcessGroupHeader(key);
            return header;
        }

        // Each header sums up the rows its group shows now, after every filter, and the groups are
        // ordered on those same rows: problems first.
        private bool UpdateGroupHeaders()
        {
            var shown  = _gridRows.Where(GridFilter.Accepts).Select(r => r.Node);
            bool moved = false;
            int order  = 0;
            foreach (var group in ProcessGroups.ByLogin(shown))
            {
                if (!_groupHeaders.TryGetValue(group.Key, out var header)) continue;
                header.Update(group);
                moved |= header.Order != order;
                header.Order = order++;
            }
            return moved;
        }

        private void GridFilter_FilterChanged(object? sender, EventArgs e)
        {
            // The filter box has refreshed the view; sum up the rows it left, and re-sort the groups if that moved them.
            if (UpdateGroupHeaders() && ChkGroupByLogin.IsChecked == true)
                _gridView.Refresh();
        }

        private void UpdateStateChips(IReadOnlyList<ProcessNode> visible)
        {
            foreach (var chip in new[] { ChipHeadBlocker, ChipBlocked, ChipActive, ChipSleeping })
            {
                var state = (string)chip.Tag;
                chip.Content   = $"{ProcessStates.Label(state)}  {visible.Count(n => n.VisualState == state):N0}";
                chip.IsChecked = !_hiddenStates.Contains(state);
            }
        }

        private void StateChip_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not ToggleButton { Tag: string state } chip) return;
            if (chip.IsChecked == true) _hiddenStates.Remove(state);
            else                        _hiddenStates.Add(state);
            Render();
        }

        private void ChkGroupByLogin_Changed(object sender, RoutedEventArgs e)
        {
            // Called while the XAML is loading, before the view exists.
            if (_gridView is null) return;

            bool grouped = ChkGroupByLogin.IsChecked == true;
            if (grouped && !_gridView.GroupDescriptions.Contains(_byLogin))
                _gridView.GroupDescriptions.Add(_byLogin);
            else if (!grouped)
                _gridView.GroupDescriptions.Remove(_byLogin);

            LoginColumn.Visibility    = grouped ? Visibility.Collapsed : Visibility.Visible;
            BtnExpandAll.IsEnabled    = grouped;
            BtnCollapseAll.IsEnabled  = grouped;
            GroupByLoginChanged?.Invoke(this, EventArgs.Empty);
        }

        private void BtnExpandAll_Click(object sender, RoutedEventArgs e)   => SetAllExpanded(true);
        private void BtnCollapseAll_Click(object sender, RoutedEventArgs e) => SetAllExpanded(false);

        private void SetAllExpanded(bool expanded)
        {
            foreach (var header in _groupHeaders.Values)
                header.IsExpanded = expanded;
        }

        // A row selected, by the mouse or the keyboard: the drawer follows (#147). A click opens it,
        // in ProcessGrid_MouseLeftButtonUp.
        private void ProcessGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_restoringSelection || ProcessGrid.SelectedItem is not ProcessGridRow row) return;
            Select(row.Node, open: false);
        }

        // A click on an underlined Blocked by or Head of chain SPID goes to that session's row.
        private void ProcessGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source
                || ItemsControl.ContainerFromElement(ProcessGrid, source) is not DataGridRow { Item: ProcessGridRow row }) return;
            var cell = FindAncestor<DataGridCell>(source);
            int? target =
                cell?.Column == BlockedByColumn && row.BlockedByIsLink ? row.BlockedBy
              : cell?.Column == HeadColumn && row.HeadIsLink           ? row.HeadOfChain
              : null;
            if (target is not { } id)
            {
                // Any other click on a row: its details, opening the drawer (#147).
                if (cell is not null)
                    Select(row.Node, open: true);
                return;
            }

            // After the click has selected the row it was on, so the jump is what stays selected.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => GoToRow(id));
        }

        private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
        {
            while (d is not null and not T)
                d = VisualTreeHelper.GetParent(d);
            return d as T;
        }

        // Selects a session's row, expanding its group: a chain can cross logins. When a filter
        // hides the row, the filters are cleared first, as the map clears its search.
        private void GoToRow(int sessionId)
        {
            if (_allNodes.All(n => n.SessionId != sessionId)) return;

            if (FindRow(sessionId) is null)
            {
                _hiddenStates.Clear();
                GridFilter.Clear();
                if (!string.IsNullOrEmpty(TxtProcSearch.Text))
                    TxtProcSearch.Text = "";   // renders again
                else
                    Render();
            }
            if (FindRow(sessionId) is not { } row) return;

            row.Group.IsExpanded = true;
            ProcessGrid.UpdateLayout();
            ProcessGrid.SelectedItems.Clear();
            ProcessGrid.SelectedItem = row;   // the drawer follows
            ProcessGrid.ScrollIntoView(row);
            Select(row.Node, open: true);
        }

        private ProcessGridRow? FindRow(int sessionId) =>
            _gridRows.FirstOrDefault(r => r.SessionId == sessionId && _gridView.Contains(r));

        private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                if (FindDescendant<T>(child) is { } deeper) return deeper;
            }
            return null;
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
            if (node.SessionId == _selected)
                StyleBall(ball, node, selected: true);
            ProcessCanvas.Children.Add(ball);
            _balls[node.SessionId] = ball;

            if (node.VisualState == BlockingChains.HeadBlocker)
            {
                int blocked = _chains.FirstOrDefault(c => c.HeadSessionId == node.SessionId)?.BlockedCount ?? 0;
                var head = new TextBlock
                {
                    Text       = blocked > 0 ? $"HEAD · {blocked}" : "HEAD",
                    Foreground = ProcessStates.BrushOf(BlockingChains.HeadBlocker),
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
                Text       = node.SessionId.ToString(CultureInfo.CurrentCulture),
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

        // A ball lit from the top left: the state's colour, lighter at the highlight and darker at the rim.
        private static (Brush fill, Color stroke) ProcessBallStyle(string state)
        {
            var color = ProcessStates.ColorOf(state);
            var fill  = new RadialGradientBrush(new GradientStopCollection
            {
                new(ThemeResources.Mix(color, Colors.White, 0.35), 0.0),
                new(ThemeResources.Mix(color, Colors.Black, 0.3), 1.0),
            }) { GradientOrigin = new Point(0.35, 0.35) };
            return (fill, color);
        }

        private ToolTip BuildProcessTooltip(ProcessNode node)
        {
            string waitInfo = node.WaitType is not null ? $"\nWait: {node.WaitType} ({node.WaitTimeMs:N0} ms)" : "";
            string blocker  = node.ParentId != 0 ? $"\nBlocked by: {BlockingChains.DescribeBlocker(node.ParentId)}" : "";
            var    chain    = _chains.FirstOrDefault(c => c.HeadSessionId == node.SessionId);
            string head     = chain is not null ? $"\nHead blocker: {chain.BlockedCount} session(s) waiting behind it" : "";
            string cmd      = string.IsNullOrWhiteSpace(node.CommandText) ? ""
                : $"\n{node.CommandText[..Math.Min(200, node.CommandText.Length)]}";

            // The app's usual tooltip, like every other one.
            var tip = new ToolTip();
            var sp = new StackPanel { Margin = new Thickness(6, 4, 6, 4) };
            sp.Children.Add(new TextBlock
            {
                Text       = $"SPID {node.SessionId}  [{node.VisualState}]",
                FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize   = 12,
            });
            sp.Children.Add(new TextBlock
            {
                Text         = $"Login: {node.UserName ?? "-"}  Host: {node.HostName ?? "-"}" +
                               $"\nProgram: {node.ProgramName ?? "-"}" +
                               $"\nStatus: {node.Status}  Open tran: {node.OpenTran}{head}{blocker}{waitInfo}{cmd}",
                FontFamily   = new FontFamily("Segoe UI"),
                FontSize     = 11,
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

            Select(node, open: true);
            e.Handled = true;
        }

        // The selected session's ball gets an accent ring; the others their state's.
        private void MarkSelected()
        {
            foreach (var (id, ball) in _balls)
                if (ball.Tag is ProcessNode node)
                    StyleBall(ball, node, id == _selected);
        }

        private static void StyleBall(Ellipse ball, ProcessNode node, bool selected)
        {
            if (selected)
            {
                ball.SetResourceReference(Shape.StrokeProperty, "Accent");
                ball.StrokeThickness = 4;
            }
            else
            {
                ball.Stroke = new SolidColorBrush(ProcessBallStyle(node.VisualState).Item2);
                ball.StrokeThickness = node.VisualState == "HeadBlocker" ? 2.5 : 1.5;
            }
        }

        private void TxtProcSearch_TextChanged(object sender, TextChangedEventArgs e)
        {
            _procSearch = TxtProcSearch.Text.Trim();
            Render();
        }

        private void ChkChainsOnly_Changed(object sender, RoutedEventArgs e)
        {
            _chainsOnly = ChkChainsOnly.IsChecked == true;
            Render();
        }

        private void BtnProcLegend_Click(object sender, RoutedEventArgs e)
        {
            ProcLegendPanel.Visibility = ProcLegendPanel.Visibility == Visibility.Visible
                ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
