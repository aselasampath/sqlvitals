using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// The details drawer (#147): the selected row's details at the right of the page, with a pin
/// button (floating ↔ pinned), a close button, and a left edge to drag. What it shows comes from
/// the page (<see cref="IDetailsSource"/>); it is only the frame.
/// </summary>
public sealed class DetailsDrawer : Border
{
    internal const string PinGlyph   = "\uE718";   // Pin
    internal const string UnpinGlyph = "\uE77A";   // Unpin
    internal const string CloseGlyph = "\uE711";   // Cancel

    private readonly TextBlock    _title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                                                   TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button       _pin;
    private readonly Button       _close;
    private readonly ContentPresenter _body = new();
    private readonly TextBlock    _hint  = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly ScrollViewer _scroll;
    private readonly Thumb        _edge;
    private bool _pinned;

    public DetailsDrawer()
    {
        BorderThickness = new Thickness(1, 0, 0, 0);
        SetResourceReference(BorderBrushProperty, "Border");
        SetResourceReference(BackgroundProperty, "BgSidebar");
        AutomationProperties.SetName(this, "Details");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

        _pin = new Button { Width = 28, Height = 28, Margin = new Thickness(6, 0, 0, 0), FontFamily = FoldSection.IconFont, FontSize = 13 };
        _pin.SetResourceReference(StyleProperty, "NavArrowButton");
        _pin.Click += (_, _) => PinRequested?.Invoke();

        _close = new Button { Content = CloseGlyph, Width = 28, Height = 28, Margin = new Thickness(4, 0, 0, 0), FontFamily = FoldSection.IconFont, FontSize = 11,
                              ToolTip = "Close the details (Ctrl+J)" };
        _close.SetResourceReference(StyleProperty, "NavArrowButton");
        AutomationProperties.SetName(_close, "Close the details");
        _close.Click += (_, _) => CloseRequested?.Invoke();

        var header = new DockPanel { Margin = new Thickness(16, 12, 10, 10) };
        DockPanel.SetDock(_close, Dock.Right);
        DockPanel.SetDock(_pin, Dock.Right);
        header.Children.Add(_close);
        header.Children.Add(_pin);
        header.Children.Add(_title);

        var footer = new TextBlock
        {
            Text = "Drag the left edge to resize · Ctrl+J opens and closes",
            FontSize = 11.5, Margin = new Thickness(16, 8, 16, 10), TextWrapping = TextWrapping.Wrap,
        };
        footer.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");

        var content = new StackPanel { Margin = new Thickness(16, 0, 16, 12) };
        content.Children.Add(_hint);
        content.Children.Add(_body);
        _scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                                     Content = content, Focusable = false };

        var separator = new Border { Height = 1 };
        separator.SetResourceReference(BackgroundProperty, "Border");

        var panel = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(separator, Dock.Bottom);
        panel.Children.Add(header);
        panel.Children.Add(footer);
        panel.Children.Add(separator);
        panel.Children.Add(_scroll);

        // The left edge: dragged, it resizes the drawer.
        _edge = new Thumb { Width = 6, Cursor = Cursors.SizeWE, HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0, Focusable = false,
                            Template = EdgeTemplate(), ToolTip = "Drag to resize" };
        _edge.DragDelta     += (_, e) => Dragged?.Invoke(-e.HorizontalChange);
        _edge.DragCompleted += (_, _) => DragCompleted?.Invoke();

        var root = new Grid();
        root.Children.Add(panel);
        root.Children.Add(_edge);
        Child = root;

        SetPinned(false);
        Show(DrawerContent.None);
    }

    /// <summary>The pin button: floating to pinned, or back.</summary>
    public event Action? PinRequested;

    public event Action? CloseRequested;

    /// <summary>The left edge dragged, by this many pixels wider (negative: narrower).</summary>
    public event Action<double>? Dragged;

    /// <summary>The drag ended: the width to remember.</summary>
    public event Action? DragCompleted;

    public DrawerContent Content { get; private set; } = DrawerContent.None;

    /// <summary>The title shown; for tests.</summary>
    internal string TitleText => _title.Text;

    /// <summary>What to select, when there are no details; empty when there are.</summary>
    internal string HintText => _hint.Visibility == Visibility.Visible ? _hint.Text : string.Empty;

    internal Button PinButton => _pin;

    internal Button CloseButton => _close;

    internal Thumb Edge => _edge;

    /// <summary>Shows a page's details, or what to select for them.</summary>
    public void Show(DrawerContent content)
    {
        Content = content;
        _title.Text = content.Title;
        AutomationProperties.SetName(this, $"Details: {content.Title}");
        _body.Content     = content.Body;
        _hint.Text        = content.Hint;
        _hint.Visibility  = content.Body is null ? Visibility.Visible : Visibility.Collapsed;
        _body.Visibility  = content.Body is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Pinned beside the page, or floating over it with a shadow.</summary>
    public void SetPinned(bool pinned)
    {
        _pinned          = pinned;
        _pin.Content     = pinned ? UnpinGlyph : PinGlyph;
        _pin.ToolTip     = pinned ? "Pinned beside the page · click to let it float over the grid" : "Floating over the grid · click to pin it beside the page";
        AutomationProperties.SetName(_pin, pinned ? "Unpin the details: float over the grid" : "Pin the details beside the page");
        Effect = pinned ? null : new DropShadowEffect { BlurRadius = 24, ShadowDepth = 0, Opacity = 0.35, Color = Colors.Black };
    }

    public bool IsPinned => _pinned;

    // An invisible strip the width of the thumb, so the whole edge can be grabbed.
    private static ControlTemplate EdgeTemplate()
    {
        var strip = new FrameworkElementFactory(typeof(Border));
        strip.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        return new ControlTemplate(typeof(Thumb)) { VisualTree = strip };
    }
}
