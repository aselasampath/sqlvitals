using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// At the right of a screen's row (#254): the worst check it explains, by its short name, and a
/// 7 px dot, both in the check's colour. Hidden while the screen has no finding.
/// </summary>
internal sealed class FindingMark : StackPanel
{
    public const double DotSize = 7;

    public FindingMark()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(6, 0, 0, 0);
        Visibility = Visibility.Collapsed;

        NameText = new TextBlock { FontFamily = new FontFamily("Segoe UI"), FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                                   VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        Dot = new Ellipse { Width = DotSize, Height = DotSize, VerticalAlignment = VerticalAlignment.Center };
        Children.Add(NameText);
        Children.Add(Dot);
    }

    internal TextBlock NameText { get; }

    internal Ellipse Dot { get; }

    /// <summary>The finding shown; null for none.</summary>
    public PageFinding? Finding { get; private set; }

    public void Show(PageFinding? finding)
    {
        Finding = finding;
        Visibility = finding is null ? Visibility.Collapsed : Visibility.Visible;
        if (finding is null)
            return;
        NameText.Text = finding.ShortName;
        // By key, so switching theme recolours it at once: the name in the check's colour as text (a
        // darker shade on the light theme), the dot in the check's own.
        NameText.SetResourceReference(TextBlock.ForegroundProperty, TextBrushKey(finding.Level));
        Dot.SetResourceReference(Shape.FillProperty, HealthBrushes.KeyFor(finding.Level));
    }

    /// <summary>A level's colour for small text: <c>HealthWarningText</c> or <c>HealthCriticalText</c>.</summary>
    public static string TextBrushKey(HealthLevel level) =>
        level is HealthLevel.Critical or HealthLevel.Unavailable ? "HealthCriticalText" : "HealthWarningText";

}
