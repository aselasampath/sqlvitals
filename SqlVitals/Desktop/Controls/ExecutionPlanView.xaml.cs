using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Engine.ExecutionPlans;
using Path = System.Windows.Shapes.Path;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// Draws an <see cref="ExecutionPlan"/> the way SSMS does: one diagram per statement, the
/// statement node on the left and data flowing right to left along arrows whose thickness
/// follows the row count. Operators can be hovered for a summary, clicked for all their
/// properties, searched, zoomed and exported as PNG.
/// </summary>
public partial class ExecutionPlanView : UserControl
{
    private const double NodeWidth  = 150;
    private const double ColumnGap  = 46;
    private const double RowGap     = 14;
    private const double IconSize   = 32;
    private const double IconTop    = 6;
    private const double HighCostPercent = 25;
    private const double ZoomMin = 0.1;
    private const double ZoomMax = 3.0;
    private const double ZoomStep = 1.2;

    private sealed class NodeVisual(PlanOperator op, Border border)
    {
        public PlanOperator Op { get; } = op;
        public Border Border { get; } = border;
        public bool IsMatch { get; set; }
    }

    private readonly List<NodeVisual> _nodes = [];
    private NodeVisual? _selected;
    private List<NodeVisual> _matches = [];
    private int _matchIndex = -1;
    private double _nodeHeight = 124;
    private double _zoom = 1;

    private Point? _panStart;
    private Point _panOrigin;

    public ExecutionPlanView()
    {
        InitializeComponent();
    }

    public bool HasDiagram => _nodes.Count > 0;

    /// <summary>Shows a parsed plan, or <paramref name="emptyMessage"/> when there is nothing to draw.</summary>
    public void Show(ExecutionPlan plan, string emptyMessage)
    {
        DiagramRoot.Children.Clear();
        _nodes.Clear();
        _matches = [];
        _matchIndex = -1;
        _selected = null;
        PropertiesTree.ItemsSource = null;
        SelectedTitle.Text = "Click an operator to see its properties.";

        if (plan.Error is not null || plan.Statements.Count == 0)
        {
            EmptyMessage.Text = plan.Error is not null
                ? $"{plan.Error}\n\nThe XML tab shows the plan exactly as SQL Server returned it."
                : emptyMessage;
            EmptyMessage.Visibility = Visibility.Visible;
            DiagramScroll.Visibility = Visibility.Collapsed;
            Toolbar.IsEnabled = false;
            PlanSummary.Text = "";
            return;
        }

        EmptyMessage.Visibility = Visibility.Collapsed;
        DiagramScroll.Visibility = Visibility.Visible;
        Toolbar.IsEnabled = true;

        // Actual plans carry two more lines per node (elapsed time, actual of estimated rows).
        _nodeHeight = plan.IsActualPlan ? 152 : 124;

        var statementWord = plan.Statements.Count == 1 ? "statement" : "statements";
        PlanSummary.Text = $"{(plan.IsActualPlan ? "Actual" : "Estimated")} execution plan · " +
                           $"{plan.Statements.Count} {statementWord} · {plan.OperatorCount:N0} operators";

        foreach (var statement in plan.Statements)
            DiagramRoot.Children.Add(BuildStatement(statement));

        UpdateMatches();
        SetZoom(1);
    }

    // ── Statement sections ───────────────────────────────────────────────

    private UIElement BuildStatement(PlanStatement statement)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 28) };

        var header = new TextBlock
        {
            Text = $"Query {statement.Number}: Query cost (relative to the batch): " +
                   $"{statement.CostPercentOfBatch.ToString("0", CultureInfo.CurrentCulture)}%",
            FontWeight = FontWeights.SemiBold,
            FontSize = 12.5,
        };
        header.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        section.Children.Add(header);

        var diagram = BuildDiagram(statement.Root);
        var textWidth = Math.Max(640, diagram.Width);

        if (!string.IsNullOrWhiteSpace(statement.Text))
        {
            var text = new TextBlock
            {
                Text = OneLine(statement.Text, 400),
                FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
                FontSize = 11.5,
                Margin = new Thickness(0, 2, 0, 0),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = textWidth,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = Truncate(statement.Text, 4000),
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            section.Children.Add(text);
        }

        foreach (var index in statement.MissingIndexes)
        {
            var hint = new TextBlock
            {
                Text = $"Missing Index (Impact {index.Impact.ToString("0.##", CultureInfo.CurrentCulture)}): {index.CreateStatement}",
                FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
                FontSize = 11.5,
                Margin = new Thickness(0, 4, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = textWidth,
                HorizontalAlignment = HorizontalAlignment.Left,
                ToolTip = "The optimizer thinks this index would lower the statement's cost by the impact shown. " +
                          "Check existing indexes before creating it. Right-click to copy.",
                ContextMenu = CopyMenu("Copy CREATE INDEX statement", index.CreateStatement),
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "Success");
            section.Children.Add(hint);
        }

        diagram.Margin = new Thickness(0, 12, 0, 0);
        diagram.HorizontalAlignment = HorizontalAlignment.Left;
        section.Children.Add(diagram);
        return section;
    }

    private Canvas BuildDiagram(PlanOperator root)
    {
        var layout = PlanTreeLayout.Compute(root);
        var columnPitch = NodeWidth + ColumnGap;
        var rowPitch = _nodeHeight + RowGap;

        var canvas = new Canvas
        {
            Width = layout.Columns * columnPitch - ColumnGap,
            Height = layout.Rows * rowPitch - RowGap,
        };

        // PlanOperator is a class without value equality, so this is keyed by instance.
        var positions = layout.Nodes.ToDictionary(
            n => n.Operator, n => new Point(n.Column * columnPitch, n.Row * rowPitch));

        // Arrows first so nodes draw on top of them.
        foreach (var placement in layout.Nodes)
            foreach (var child in placement.Operator.Children)
                AddArrow(canvas, positions[placement.Operator], positions[child], child);

        foreach (var placement in layout.Nodes)
        {
            var position = positions[placement.Operator];
            var node = BuildNode(placement.Operator);
            Canvas.SetLeft(node, position.X);
            Canvas.SetTop(node, position.Y);
            canvas.Children.Add(node);
        }
        return canvas;
    }

    // ── Operator nodes ───────────────────────────────────────────────────

    private Border BuildNode(PlanOperator op)
    {
        var panel = new StackPanel();

        var iconArea = new Grid { Width = IconSize + 14, Height = IconSize + 4, Margin = new Thickness(0, IconTop - 2, 0, 2) };
        var icon = new Path
        {
            Data = PlanOperatorIcons.For(op),
            Width = IconSize,
            Height = IconSize,
            StrokeThickness = 1.7,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        icon.SetResourceReference(Shape.StrokeProperty, op.IsStatementRoot ? "TextMuted" : "Accent");
        iconArea.Children.Add(icon);

        if (op.Warnings.Count > 0)
            iconArea.Children.Add(WarningBadge());
        if (op.IsParallel)
            iconArea.Children.Add(ParallelBadge());
        panel.Children.Add(iconArea);

        panel.Children.Add(NodeText(op.PhysicalOp, bold: true, wrap: true));
        if (op.Subtitle is { Length: > 0 } subtitle)
            panel.Children.Add(NodeText($"({subtitle})", muted: true));
        if (op.ObjectName is not null)
            panel.Children.Add(NodeText(op.ObjectName, muted: true));
        panel.Children.Add(NodeText($"Cost: {op.CostPercent.ToString("0", CultureInfo.CurrentCulture)} %",
                                    bold: op.CostPercent >= HighCostPercent));

        if (op.HasActuals)
        {
            if (op.ActualElapsedMs is { } ms)
                panel.Children.Add(NodeText((ms / 1000.0).ToString("0.000", CultureInfo.CurrentCulture) + "s", muted: true));

            var estimate = op.EstimatedRowsAllExecutions;
            var pct = estimate > 0 ? op.ActualRows!.Value / estimate * 100 : 0;
            var rows = NodeText($"{op.ActualRows!.Value:N0} of {estimate:N0} ({pct:N0}%)", bold: op.IsBadEstimate);
            if (op.IsBadEstimate) rows.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            panel.Children.Add(rows);
        }

        var node = new Border
        {
            Width = NodeWidth,
            Height = _nodeHeight,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(4, 0, 4, 2),
            Cursor = Cursors.Hand,
            Child = panel,
            Tag = op,
        };

        var visual = new NodeVisual(op, node);
        _nodes.Add(visual);
        ApplyNodeStyle(visual);

        node.MouseLeftButtonDown += (_, e) =>
        {
            Select(visual);
            e.Handled = true;
        };

        var tip = ThemedToolTip();
        tip.Opened += (_, _) => tip.Content ??= BuildOperatorTip(op);
        node.ToolTip = tip;
        ToolTipService.SetInitialShowDelay(node, 350);
        ToolTipService.SetShowDuration(node, 60_000);
        return node;
    }

    private static TextBlock NodeText(string text, bool bold = false, bool muted = false, bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MaxHeight = wrap ? 30 : double.PositiveInfinity,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        block.SetResourceReference(TextBlock.ForegroundProperty, muted ? "TextMuted" : "TextPrimary");
        return block;
    }

    private static UIElement WarningBadge()
    {
        var badge = new Grid
        {
            Width = 15, Height = 14,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "This operator has warnings. Hover over it for details.",
        };
        var triangle = new Path { Data = Geometry.Parse("M7.5,0.5 L14.5,13.5 H0.5 Z"), Stroke = Brushes.Black, StrokeThickness = 0.6 };
        triangle.SetResourceReference(Shape.FillProperty, "Warning");
        badge.Children.Add(triangle);
        badge.Children.Add(new TextBlock
        {
            Text = "!", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = Brushes.Black,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, -1),
        });
        return badge;
    }

    private static UIElement ParallelBadge()
    {
        var badge = new Grid
        {
            Width = 15, Height = 15,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            ToolTip = "Runs in parallel",
        };
        var circle = new Ellipse { Stroke = Brushes.Black, StrokeThickness = 0.6 };
        circle.SetResourceReference(Shape.FillProperty, "Warning");
        badge.Children.Add(circle);
        badge.Children.Add(new Path
        {
            Data = Geometry.Parse("M3.5,5.5 H10.5 M8.5,3.5 L10.5,5.5 L8.5,7.5 M4.5,9.5 H11.5 M9.5,7.5 L11.5,9.5 L9.5,11.5"),
            Stroke = Brushes.Black, StrokeThickness = 1.1,
        });
        return badge;
    }

    private void ApplyNodeStyle(NodeVisual visual)
    {
        var border = visual.Border;
        var highCost = !visual.Op.IsStatementRoot && visual.Op.CostPercent >= HighCostPercent;

        if (ReferenceEquals(visual, _selected))
        {
            border.SetResourceReference(Border.BorderBrushProperty, "Accent");
            border.Background = Tint("AccentColor", 0x30);
        }
        else if (visual.IsMatch)
        {
            border.SetResourceReference(Border.BorderBrushProperty, "AccentLight");
            border.Background = Tint("AccentLightColor", 0x24);
        }
        else if (highCost)
        {
            border.SetResourceReference(Border.BorderBrushProperty, "Warning");
            border.Background = Tint("WarningColor", 0x24);
        }
        else
        {
            border.BorderBrush = Brushes.Transparent;
            border.Background = Brushes.Transparent;    // still hit-testable
        }
    }

    private static Brush Tint(string colorKey, byte alpha)
    {
        var color = Application.Current.Resources[colorKey] is Color c ? c : Colors.Gray;
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    // ── Arrows ───────────────────────────────────────────────────────────

    private void AddArrow(Canvas canvas, Point parent, Point child, PlanOperator childOp)
    {
        var rows = childOp.HasActuals ? childOp.ActualRows!.Value : childOp.EstimatedRowsAllExecutions;
        var thickness = ArrowThickness(rows);

        var parentY = parent.Y + IconTop + IconSize / 2;
        var childY = child.Y + IconTop + IconSize / 2;
        var tipX = parent.X + NodeWidth / 2 + IconSize / 2 + 6;
        var startX = child.X + NodeWidth / 2 - IconSize / 2 - 6;
        var bendX = parent.X + NodeWidth + ColumnGap / 2;
        var head = Math.Max(7, thickness + 5);

        var line = new StreamGeometry();
        using (var ctx = line.Open())
        {
            ctx.BeginFigure(new Point(startX, childY), false, false);
            ctx.LineTo(new Point(bendX, childY), true, true);
            ctx.LineTo(new Point(bendX, parentY), true, true);
            ctx.LineTo(new Point(tipX + head, parentY), true, true);
        }
        line.Freeze();

        var shaft = new Path { Data = line, StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Miter, Opacity = 0.75 };
        shaft.SetResourceReference(Shape.StrokeProperty, "TextMuted");
        canvas.Children.Add(shaft);

        var arrowHead = new Polygon
        {
            Points = [new Point(tipX, parentY), new Point(tipX + head, parentY - head * 0.6), new Point(tipX + head, parentY + head * 0.6)],
            Opacity = 0.75,
        };
        arrowHead.SetResourceReference(Shape.FillProperty, "TextMuted");
        canvas.Children.Add(arrowHead);

        // A wider invisible stroke on top so thin arrows are easy to hover.
        var hitArea = new Path { Data = line, StrokeThickness = thickness + 10, Stroke = Brushes.Transparent };
        var tip = ThemedToolTip();
        tip.Opened += (_, _) => tip.Content ??= BuildArrowTip(childOp);
        hitArea.ToolTip = tip;
        ToolTipService.SetInitialShowDelay(hitArea, 250);
        canvas.Children.Add(hitArea);
    }

    /// <summary>1 px for a single row, growing with the order of magnitude, like SSMS.</summary>
    internal static double ArrowThickness(double rows) =>
        rows <= 1 ? 1 : Math.Min(12, 1 + Math.Log10(rows) * 1.6);

    // ── Tooltips ─────────────────────────────────────────────────────────

    private static ToolTip ThemedToolTip()
    {
        var tip = new ToolTip { Padding = new Thickness(10, 8, 10, 8), MaxWidth = 460 };
        tip.SetResourceReference(Control.BackgroundProperty, "BgCard");
        tip.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        tip.SetResourceReference(Control.BorderBrushProperty, "Border");
        return tip;
    }

    private static UIElement BuildOperatorTip(PlanOperator op)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = op.PhysicalOp, FontWeight = FontWeights.SemiBold, FontSize = 13 });
        if (op.Subtitle is { Length: > 0 } subtitle)
            panel.Children.Add(TipMuted(subtitle));

        var rows = new List<(string, string)>();
        if (!op.IsStatementRoot)
        {
            rows.Add(("Physical Operation", op.PhysicalOp));
            rows.Add(("Logical Operation", op.LogicalOp));
            if ((op.ActualExecutionMode ?? op.EstimatedExecutionMode) is { } mode)
                rows.Add(("Execution Mode", mode));
            if (op.HasActuals)
            {
                rows.Add(("Actual Number of Rows", op.ActualRows!.Value.ToString("N0", CultureInfo.CurrentCulture)));
                rows.Add(("Actual Number of Executions", (op.ActualExecutions ?? 0).ToString("N0", CultureInfo.CurrentCulture)));
                if (op.ActualElapsedMs is { } ms)
                    rows.Add(("Actual Elapsed Time", $"{ms:N0} ms"));
            }
            rows.Add(("Estimated Operator Cost", $"{Cost(op.EstimatedOperatorCost)} ({op.CostPercent:0}%)"));
            rows.Add(("Estimated I/O Cost", Cost(op.EstimateIO)));
            rows.Add(("Estimated CPU Cost", Cost(op.EstimateCPU)));
            rows.Add(("Estimated Subtree Cost", Cost(op.EstimatedSubtreeCost)));
            rows.Add(("Estimated Number of Executions", Rows(op.EstimatedExecutions)));
            rows.Add(("Estimated Number of Rows Per Execution", Rows(op.EstimateRows)));
            rows.Add(("Estimated Number of Rows for All Executions", Rows(op.EstimatedRowsAllExecutions)));
            rows.Add(("Estimated Row Size", $"{Rows(op.AvgRowSize)} B"));
            if (op.Ordered is { } ordered)
                rows.Add(("Ordered", ordered ? "True" : "False"));
            rows.Add(("Parallel", op.IsParallel ? "True" : "False"));
            rows.Add(("Node ID", op.NodeId.ToString(CultureInfo.CurrentCulture)));
        }
        else
        {
            rows.Add(("Estimated Subtree Cost", Cost(op.EstimatedSubtreeCost)));
            rows.Add(("Estimated Number of Rows", Rows(op.EstimateRows)));
        }
        panel.Children.Add(TipGrid(rows));

        if (op.IsBadEstimate)
        {
            var factor = op.EstimateErrorFactor!.Value.ToString("N0", CultureInfo.CurrentCulture);
            var direction = op.ActualRows!.Value > op.EstimatedRowsAllExecutions ? "more" : "fewer";
            var note = new TextBlock
            {
                Text = $"Bad estimate: {factor}× {direction} rows than estimated. Out-of-date statistics, " +
                       "parameter sniffing or complex predicates are common causes.",
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0), FontWeight = FontWeights.SemiBold,
            };
            note.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
            panel.Children.Add(note);
        }

        AddTipSection(panel, "Object", op.ObjectName);
        AddTipSection(panel, "Seek Predicates", op.SeekPredicate);
        AddTipSection(panel, "Predicate", op.Predicate);
        AddTipSection(panel, "Output List", op.OutputList is null ? null : Truncate(op.OutputList, 600));

        if (op.Warnings.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "Warnings", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2) });
            foreach (var warning in op.Warnings)
            {
                var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 1, 0, 1) };
                var glyph = new Run("▲ ");
                glyph.SetResourceReference(TextElement.ForegroundProperty, "Warning");
                line.Inlines.Add(glyph);
                line.Inlines.Add(new Run(warning.Message));
                panel.Children.Add(line);
            }
        }

        panel.Children.Add(TipMuted("Click for all properties."));
        return panel;
    }

    private static UIElement BuildArrowTip(PlanOperator from)
    {
        var rows = new List<(string, string)>();
        if (from.HasActuals)
            rows.Add(("Actual Number of Rows", from.ActualRows!.Value.ToString("N0", CultureInfo.CurrentCulture)));
        rows.Add(("Estimated Number of Rows Per Execution", Rows(from.EstimateRows)));
        rows.Add(("Estimated Number of Rows for All Executions", Rows(from.EstimatedRowsAllExecutions)));
        rows.Add(("Estimated Row Size", $"{Rows(from.AvgRowSize)} B"));
        var dataRows = from.HasActuals ? from.ActualRows!.Value : from.EstimatedRowsAllExecutions;
        rows.Add((from.HasActuals ? "Data Size" : "Estimated Data Size", Bytes(dataRows * from.AvgRowSize)));

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = $"From {from.PhysicalOp}", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(TipGrid(rows));
        return panel;
    }

    private static Grid TipGrid(List<(string Label, string Value)> rows)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 60 });
        for (int i = 0; i < rows.Count; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = TipMuted(rows[i].Label);
            label.Margin = new Thickness(0, 1, 16, 1);
            Grid.SetRow(label, i);
            grid.Children.Add(label);

            var value = new TextBlock { Text = rows[i].Value, TextAlignment = TextAlignment.Right, Margin = new Thickness(0, 1, 0, 1) };
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
        }
        return grid;
    }

    private static void AddTipSection(StackPanel panel, string title, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 1) });
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
    }

    private static TextBlock TipMuted(string text)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        return block;
    }

    private static string Cost(double value) => value.ToString("0.#######", CultureInfo.CurrentCulture);

    private static string Rows(double value) => value.ToString("#,0.###", CultureInfo.CurrentCulture);

    private static string Bytes(double bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (1L << 30):N1} GB",
        >= 1L << 20 => $"{bytes / (1L << 20):N1} MB",
        >= 1L << 10 => $"{bytes / (1L << 10):N0} KB",
        _ => $"{bytes:N0} B",
    };

    // ── Selection and properties ─────────────────────────────────────────

    private void Select(NodeVisual visual)
    {
        var previous = _selected;
        _selected = visual;
        if (previous is not null) ApplyNodeStyle(previous);
        ApplyNodeStyle(visual);

        var op = visual.Op;
        SelectedTitle.Text = op.Subtitle is { Length: > 0 } s ? $"{op.PhysicalOp} ({s})" : op.PhysicalOp;
        PropertiesTree.ItemsSource = op.Properties;
    }

    private void CopyProperty_Click(object sender, RoutedEventArgs e)
    {
        if (PropertiesTree.SelectedItem is PlanProperty p)
            ClipboardHelper.SetText(p.Value is null ? p.Name : $"{p.Name}: {p.Value}");
    }

    private static ContextMenu CopyMenu(string header, string text)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => ClipboardHelper.SetText(text);
        var menu = new ContextMenu();
        menu.Items.Add(item);
        return menu;
    }

    // ── Find ─────────────────────────────────────────────────────────────

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateMatches();

    private void UpdateMatches()
    {
        var term = FindBox.Text;
        _matches = [];
        _matchIndex = -1;
        foreach (var visual in _nodes)
        {
            visual.IsMatch = visual.Op.Matches(term);
            if (visual.IsMatch) _matches.Add(visual);
            ApplyNodeStyle(visual);
        }

        MatchLabel.Text = string.IsNullOrWhiteSpace(term) ? ""
            : _matches.Count == 0 ? "No matches"
            : _matches.Count == 1 ? "1 match"
            : $"{_matches.Count} matches";
    }

    private void FindBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            FindBox.Clear();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _matches.Count > 0)
        {
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
            _matchIndex = ((_matchIndex + step) % _matches.Count + _matches.Count) % _matches.Count;
            var match = _matches[_matchIndex];
            Select(match);
            match.Border.BringIntoView();
            MatchLabel.Text = $"{_matchIndex + 1} of {_matches.Count}";
            e.Handled = true;
        }
    }

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || !HasDiagram) return;

        switch (e.Key)
        {
            case Key.F:
                FindBox.Focus();
                FindBox.SelectAll();
                break;
            case Key.OemPlus or Key.Add:
                SetZoom(_zoom * ZoomStep);
                break;
            case Key.OemMinus or Key.Subtract:
                SetZoom(_zoom / ZoomStep);
                break;
            case Key.D0 or Key.NumPad0:
                SetZoom(1);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ── Zoom and pan ─────────────────────────────────────────────────────

    private void SetZoom(double zoom, Point? anchor = null)
    {
        zoom = Math.Clamp(zoom, ZoomMin, ZoomMax);

        // Keep the content under the anchor (the mouse) where it is.
        var viewPoint = anchor ?? new Point(DiagramScroll.ViewportWidth / 2, DiagramScroll.ViewportHeight / 2);
        var contentX = (DiagramScroll.HorizontalOffset + viewPoint.X) / _zoom;
        var contentY = (DiagramScroll.VerticalOffset + viewPoint.Y) / _zoom;

        _zoom = zoom;
        ZoomTransform.ScaleX = zoom;
        ZoomTransform.ScaleY = zoom;
        ZoomLabel.Text = zoom.ToString("P0", CultureInfo.CurrentCulture);

        DiagramScroll.UpdateLayout();
        DiagramScroll.ScrollToHorizontalOffset(contentX * zoom - viewPoint.X);
        DiagramScroll.ScrollToVerticalOffset(contentY * zoom - viewPoint.Y);
    }

    private void BtnZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom * ZoomStep);

    private void BtnZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(_zoom / ZoomStep);

    private void BtnZoomReset_Click(object sender, RoutedEventArgs e) => SetZoom(1);

    private void BtnZoomFit_Click(object sender, RoutedEventArgs e)
    {
        DiagramRoot.UpdateLayout();
        var width = DiagramRoot.ActualWidth + DiagramRoot.Margin.Left + DiagramRoot.Margin.Right;
        var height = DiagramRoot.ActualHeight + DiagramRoot.Margin.Top + DiagramRoot.Margin.Bottom;
        if (width <= 0 || height <= 0) return;

        var fit = Math.Min(DiagramScroll.ActualWidth / width, DiagramScroll.ActualHeight / height) * 0.98;
        SetZoom(Math.Min(1, fit));
        DiagramScroll.ScrollToHome();
    }

    private void DiagramScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        SetZoom(e.Delta > 0 ? _zoom * ZoomStep : _zoom / ZoomStep, e.GetPosition(DiagramScroll));
        e.Handled = true;
    }

    // Drag the background (or anywhere with the middle button) to pan.
    private void DiagramScroll_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        bool onScrollBar = FindAncestor<ScrollBar>(source) is not null;
        bool onNode = FindAncestor<Border>(source, b => b.Tag is PlanOperator) is not null;

        bool pan = e.ChangedButton == MouseButton.Middle
                   || (e.ChangedButton == MouseButton.Left && !onNode && !onScrollBar);
        if (!pan || onScrollBar) return;

        _panStart = e.GetPosition(DiagramScroll);
        _panOrigin = new Point(DiagramScroll.HorizontalOffset, DiagramScroll.VerticalOffset);
        DiagramScroll.CaptureMouse();
        DiagramScroll.Cursor = Cursors.SizeAll;
        e.Handled = e.ChangedButton == MouseButton.Middle;
    }

    private void DiagramScroll_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_panStart is not { } start) return;
        var now = e.GetPosition(DiagramScroll);
        DiagramScroll.ScrollToHorizontalOffset(_panOrigin.X - (now.X - start.X));
        DiagramScroll.ScrollToVerticalOffset(_panOrigin.Y - (now.Y - start.Y));
    }

    private void DiagramScroll_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panStart is null) return;
        DiagramScroll.ReleaseMouseCapture();
    }

    private void DiagramScroll_LostMouseCapture(object sender, MouseEventArgs e)
    {
        _panStart = null;
        DiagramScroll.Cursor = null;
    }

    private static T? FindAncestor<T>(DependencyObject? node, Func<T, bool>? match = null) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T t && (match is null || match(t))) return t;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    // ── Export ───────────────────────────────────────────────────────────

    /// <summary>Saves the whole diagram, at actual size, as a PNG.</summary>
    public void ExportPng(string path)
    {
        DiagramRoot.UpdateLayout();
        const double pad = 16;
        var width = DiagramRoot.ActualWidth;
        var height = DiagramRoot.ActualHeight;

        // Very large plans are scaled down to stay within what a bitmap can hold.
        var scale = Math.Min(1, 16_000 / Math.Max(width + pad * 2, height + pad * 2));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawRectangle((Brush)FindResource("BgCard"), null, new Rect(0, 0, width + pad * 2, height + pad * 2));
            var brush = new VisualBrush(DiagramRoot)
            {
                Stretch = Stretch.None,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, width, height),
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            dc.DrawRectangle(brush, null, new Rect(pad, pad, width, height));
        }

        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling((width + pad * 2) * scale), (int)Math.Ceiling((height + pad * 2) * scale),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    // ── Text helpers ─────────────────────────────────────────────────────

    private static string OneLine(string text, int max) =>
        Truncate(string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), max);

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
