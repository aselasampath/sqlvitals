using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Media;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// A summary section of a <see cref="PageShell"/> (#146): charts or KPI cards under a header that
/// folds them to one line, with what they say in brief while folded. The page remembers the fold.
/// </summary>
[ContentProperty(nameof(Body))]
public sealed class FoldSection : Decorator
{
    /// <summary>The Fluent icons the chevrons are drawn with, MDL2 on Windows 10.</summary>
    internal static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    internal const string Unfolded = "\uE70D";   // ChevronDown
    internal const string Folded   = "\uE76C";   // ChevronRight

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(string), typeof(FoldSection),
            new PropertyMetadata(string.Empty, (d, _) => ((FoldSection)d).ShowHeader()));

    public static readonly DependencyProperty FoldedSummaryProperty =
        DependencyProperty.Register(nameof(FoldedSummary), typeof(string), typeof(FoldSection),
            new PropertyMetadata(null, (d, _) => ((FoldSection)d).ShowHeader()));

    public static readonly DependencyProperty IsFoldedProperty =
        DependencyProperty.Register(nameof(IsFolded), typeof(bool), typeof(FoldSection),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                (d, e) => ((FoldSection)d).OnFoldedChanged((bool)e.NewValue)));

    private readonly ToggleButton _toggle;
    private readonly TextBlock    _chevron = new() { FontFamily = IconFont, FontSize = 10, Width = 18, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock    _header  = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock    _summary = new() { FontSize = 12, Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                                                     TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border       _body    = new() { Margin = new Thickness(0, 6, 0, 0) };

    public FoldSection()
    {
        _chevron.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        _header.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

        var row = new DockPanel { LastChildFill = true, Background = Brushes.Transparent };
        DockPanel.SetDock(_chevron, Dock.Left);
        DockPanel.SetDock(_header, Dock.Left);
        row.Children.Add(_chevron);
        row.Children.Add(_header);
        row.Children.Add(_summary);

        _toggle = new ToggleButton
        {
            Content                    = row,
            Template                   = FlatTemplate(),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Cursor                     = System.Windows.Input.Cursors.Hand,
            Focusable                  = true,
            IsChecked                  = true,
        };
        _toggle.Checked   += (_, _) => IsFolded = false;
        _toggle.Unchecked += (_, _) => IsFolded = true;

        var panel = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(_toggle, Dock.Top);
        panel.Children.Add(_toggle);
        panel.Children.Add(_body);
        Child = panel;
        ShowHeader();
    }

    /// <summary>The name the page remembers the fold by, e.g. "summary". Unique on its page.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The section's name, on its one-line header: "Summary", "Health and settings".</summary>
    public string Header
    {
        get => (string)GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    /// <summary>What the section says, in one line, shown beside the header while it is folded.</summary>
    public string? FoldedSummary
    {
        get => (string?)GetValue(FoldedSummaryProperty);
        set => SetValue(FoldedSummaryProperty, value);
    }

    public bool IsFolded
    {
        get => (bool)GetValue(IsFoldedProperty);
        set => SetValue(IsFoldedProperty, value);
    }

    /// <summary>The section's content: the charts or cards.</summary>
    public UIElement? Body
    {
        get => _body.Child;
        set => _body.Child = value;
    }

    /// <summary>The user folded or unfolded it, for the page to remember.</summary>
    public event Action<FoldSection, bool>? FoldChanged;

    /// <summary>The header's toggle; for tests and the page's keyboard.</summary>
    internal ToggleButton Toggle => _toggle;

    // Set from the remembered folds: not a change to save.
    internal bool Restoring { get; set; }

    private void OnFoldedChanged(bool folded)
    {
        _toggle.IsChecked = !folded;
        _body.Visibility  = folded ? Visibility.Collapsed : Visibility.Visible;
        ShowHeader();
        if (!Restoring)
            FoldChanged?.Invoke(this, folded);
    }

    private void ShowHeader()
    {
        var folded = IsFolded;
        _chevron.Text       = folded ? Folded : Unfolded;
        _header.Text        = Header;
        _summary.Text       = folded ? FoldedSummary ?? string.Empty : string.Empty;
        _summary.Visibility = folded && !string.IsNullOrEmpty(FoldedSummary) ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_toggle, folded ? $"{Header}, folded: {FoldedSummary}" : $"{Header}, unfolded");
        _toggle.ToolTip = folded ? $"Unfold {Header}" : $"Fold {Header} to one line";
    }

    // A toggle without a button's chrome: the header row, with a focus line for the keyboard.
    internal static ControlTemplate FlatTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.PaddingProperty, new Thickness(0, 2, 0, 2));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        border.AppendChild(content);
        return new ControlTemplate(typeof(ToggleButton)) { VisualTree = border };
    }
}
