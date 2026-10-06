using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Controls;

/// <summary>The pieces the details drawer's content is built from (#147), so every page's looks the same.</summary>
internal static class DrawerParts
{
    /// <summary>A section's heading: "Last statement", "Plans".</summary>
    public static TextBlock Heading(string text)
    {
        var heading = new TextBlock { Text = text, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 6) };
        heading.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        return heading;
    }

    /// <summary>A muted line under a section, hidden until it has something to say.</summary>
    public static TextBlock Note()
    {
        var note = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        note.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        return note;
    }

    /// <summary>A read-only box of T-SQL, in a fixed-width font, wrapped.</summary>
    public static TextBox Code(double maxHeight)
    {
        var box = new TextBox
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MaxHeight = maxHeight, Padding = new Thickness(8),
            FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11.5,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(1),
        };
        box.SetResourceReference(Control.BackgroundProperty, "BgDeep");
        box.SetResourceReference(Control.ForegroundProperty, "TextPrimary");
        box.SetResourceReference(Control.BorderBrushProperty, "Border");
        return box;
    }

    public static Button Button(string text, Action click, string toolTip)
    {
        var button = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 6), ToolTip = toolTip };
        button.SetResourceReference(FrameworkElement.StyleProperty, "SecondaryButton");
        button.Click += (_, _) => click();
        return button;
    }

    public static WrapPanel Row(params Button[] buttons)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        foreach (var button in buttons)
            row.Children.Add(button);
        return row;
    }

    /// <summary>A two-column grid of labels and values; a warning value in amber.</summary>
    public static Grid Rows() => new()
    {
        Margin = new Thickness(0, 0, 0, 4),
        ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(118) }, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } },
    };

    /// <summary>Puts rows in a grid from <see cref="Rows()"/>, replacing what it had.</summary>
    public static void Fill(Grid grid, IEnumerable<DetailRow> rows)
    {
        grid.Children.Clear();
        grid.RowDefinitions.Clear();
        foreach (var row in rows)
        {
            var index = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = row.Label, FontSize = 12, Margin = new Thickness(0, 2, 8, 2), TextWrapping = TextWrapping.Wrap };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
            var value = new TextBlock { Text = row.Value, FontSize = 12, Margin = new Thickness(0, 2, 0, 2), TextWrapping = TextWrapping.Wrap };
            if (row.Warning)
                value.Foreground = HealthBrushes.Amber;
            else
                value.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            Grid.SetRow(label, index);
            Grid.SetRow(value, index);
            Grid.SetColumn(value, 1);
            grid.Children.Add(label);
            grid.Children.Add(value);
        }
    }

    /// <summary>
    /// A grid whose selected row fills the drawer (#147, #149): a row selected shows it, a click on
    /// one also opens a closed drawer.
    /// </summary>
    /// <param name="cleared">Run when no row is selected any more, e.g. a filter hid it (#150).</param>
    public static void FollowSelection(DataGrid grid, Action<object, bool> show, Action? cleared = null)
    {
        grid.SelectionChanged += (_, _) =>
        {
            if (grid.SelectedItem is { } row)
                show(row, false);
            else
                cleared?.Invoke();
        };
        grid.AddHandler(UIElement.MouseLeftButtonUpEvent, new System.Windows.Input.MouseButtonEventHandler((_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(grid, source) is DataGridRow { Item: { } row })
                show(row, true);
        }), handledEventsToo: true);
    }

    /// <summary>"Label: value" for each row of a grid from <see cref="Fill"/>; for tests.</summary>
    public static IReadOnlyList<string> Texts(Grid grid) =>
        grid.Children.OfType<TextBlock>().Chunk(2).Select(pair => $"{pair[0].Text}: {pair[1].Text}").ToList();
}
