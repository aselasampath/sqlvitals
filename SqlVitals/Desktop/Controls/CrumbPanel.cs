using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// The top bar's breadcrumb (#140): its parts in a row with a "›" between them. Short of room, it
/// leaves out the parts marked <see cref="CanDropProperty"/>, first to last; then the parts trim
/// from the end, so the page's name goes before the server's.
/// </summary>
public sealed class CrumbPanel : Panel
{
    private const double Gap = 6;
    private const string Separator = "›";

    public static readonly DependencyProperty CanDropProperty = DependencyProperty.RegisterAttached(
        "CanDrop", typeof(bool), typeof(CrumbPanel),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static bool GetCanDrop(UIElement element) => (bool)element.GetValue(CanDropProperty);
    public static void SetCanDrop(UIElement element, bool value) => element.SetValue(CanDropProperty, value);

    public static readonly DependencyProperty SeparatorBrushProperty = DependencyProperty.Register(
        nameof(SeparatorBrush), typeof(Brush), typeof(CrumbPanel),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush SeparatorBrush
    {
        get => (Brush)GetValue(SeparatorBrushProperty);
        set => SetValue(SeparatorBrushProperty, value);
    }

    // The parts shown, and where their separators go, from the last layout.
    private readonly List<UIElement> _shown = [];
    private readonly List<double>    _separators = [];

    /// <summary>
    /// Room left over, or short (negative), once every part that can be left out is: the top bar
    /// gives up other things before the server's or the page's name trims.
    /// </summary>
    internal double Spare { get; private set; }

    /// <summary>The width of the parts that are never left out, and the separators between them.</summary>
    internal double KeptWidth { get; private set; }

    /// <summary>The parts left out for want of room, for tests.</summary>
    internal IEnumerable<UIElement> Dropped => InternalChildren.Cast<UIElement>().Except(_shown);

    private FormattedText SeparatorText() => new(Separator, CultureInfo.CurrentUICulture, FlowDirection,
        new Typeface(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
        TextElement.GetFontSize(this), SeparatorBrush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private double SeparatorWidth => SeparatorText().Width + 2 * Gap;

    protected override Size MeasureOverride(Size available)
    {
        var children = InternalChildren.Cast<UIElement>().ToList();
        foreach (var child in children)
            child.Measure(new Size(double.PositiveInfinity, available.Height));

        _shown.Clear();
        _shown.AddRange(children);
        var separator = SeparatorWidth;
        double Width() => _shown.Sum(c => c.DesiredSize.Width) + separator * Math.Max(0, _shown.Count - 1);

        while (Width() > available.Width && _shown.Take(_shown.Count - 1).FirstOrDefault(GetCanDrop) is { } drop)
            _shown.Remove(drop);

        var kept = children.Where(c => !GetCanDrop(c)).ToList();
        KeptWidth = kept.Sum(c => c.DesiredSize.Width) + separator * Math.Max(0, kept.Count - 1);
        Spare = available.Width - KeptWidth;

        // Each part in turn takes its width while there is room; the one that reaches the end
        // trims to what is left, and any after it go.
        double x = 0;
        for (var i = 0; i < _shown.Count; i++)
        {
            var left = available.Width - x - (i > 0 ? separator : 0);
            if (left < 1)
            {
                _shown.RemoveRange(i, _shown.Count - i);
                break;
            }
            if (_shown[i].DesiredSize.Width > left)
                _shown[i].Measure(new Size(left, available.Height));
            x += (i > 0 ? separator : 0) + _shown[i].DesiredSize.Width;
        }

        var height = children.Count == 0 ? 0 : children.Max(c => c.DesiredSize.Height);
        return new Size(Math.Min(Width(), double.IsInfinity(available.Width) ? Width() : available.Width), height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        _separators.Clear();
        var separator = SeparatorWidth;
        double x = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (!_shown.Contains(child))
            {
                child.Arrange(new Rect(0, 0, 0, 0));
                continue;
            }
            if (x > 0)
            {
                _separators.Add(x + Gap);
                x += separator;
            }
            var width = Math.Min(child.DesiredSize.Width, Math.Max(0, final.Width - x));
            child.Arrange(new Rect(x, (final.Height - child.DesiredSize.Height) / 2, width, child.DesiredSize.Height));
            x += width;
        }
        InvalidateVisual();
        return final;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        foreach (var x in _separators)
        {
            var text = SeparatorText();
            dc.DrawText(text, new Point(x, (RenderSize.Height - text.Height) / 2));
        }
    }
}
