using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// A page's verdict (#146), first under its title: an icon in the severity's colour, one line
/// saying what is wrong or that nothing is, a line of why, and up to four actions. Folded, only
/// the icon and the first line are left.
/// </summary>
public sealed class VerdictView : Border
{
    private readonly Grid       _icon      = new() { Width = 28, Height = 28, VerticalAlignment = VerticalAlignment.Top };
    private readonly Ellipse    _iconDisc  = new();
    private readonly TextBlock  _iconGlyph = new() { FontWeight = FontWeights.Bold, FontSize = 14,
                                                     HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock  _headline  = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock  _why       = new() { FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly WrapPanel  _actions   = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly ToggleButton _fold;
    private readonly TextBlock  _chevron   = new() { FontFamily = FoldSection.IconFont, FontSize = 10, VerticalAlignment = VerticalAlignment.Center };

    private PageVerdict? _verdict;
    private bool _folded;

    public VerdictView()
    {
        CornerRadius    = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        Padding         = new Thickness(14, 12, 14, 12);
        Margin          = new Thickness(0, 0, 0, 12);
        SetResourceReference(BackgroundProperty, "BgCard");
        _headline.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        _why.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        _chevron.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        AutomationProperties.SetName(this, "Verdict");

        _icon.Children.Add(_iconDisc);
        _icon.Children.Add(_iconGlyph);

        _fold = new ToggleButton
        {
            Content   = _chevron,
            Template  = FoldSection.FlatTemplate(),
            Width     = 24,
            Height    = 24,
            Cursor    = Cursors.Hand,
            IsChecked = true,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        _fold.Checked   += (_, _) => SetFolded(false, byUser: true);
        _fold.Unchecked += (_, _) => SetFolded(true, byUser: true);

        var text = new StackPanel { Margin = new Thickness(12, 3, 8, 0) };
        text.Children.Add(_headline);
        text.Children.Add(_why);
        text.Children.Add(_actions);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(text, 1);
        Grid.SetColumn(_fold, 2);
        grid.Children.Add(_icon);
        grid.Children.Add(text);
        grid.Children.Add(_fold);
        Child = grid;

        Show(null);
    }

    /// <summary>An action pressed, for the page to act on.</summary>
    public event Action<VerdictAction>? ActionRequested;

    /// <summary>The user folded or unfolded it, for the page to remember.</summary>
    public event Action<bool>? FoldChanged;

    /// <summary>Run with the <see cref="VerdictAction"/> pressed, as well as <see cref="ActionRequested"/>.</summary>
    public ICommand? Command { get; set; }

    public PageVerdict? Verdict => _verdict;

    public bool IsFolded => _folded;

    /// <summary>The action buttons shown; for tests.</summary>
    internal IReadOnlyList<Button> ActionButtons => _actions.Children.OfType<Button>().ToList();

    internal ToggleButton FoldToggle => _fold;

    /// <summary>Shows a verdict; collapses for none.</summary>
    public void Show(PageVerdict? verdict)
    {
        _verdict   = verdict;
        Visibility = verdict is null ? Visibility.Collapsed : Visibility.Visible;
        if (verdict is null)
            return;

        var brush = Brush(verdict.Level);
        _iconDisc.Fill = Tint(brush);
        _iconGlyph.Foreground = brush;
        _iconGlyph.Text = verdict.Level switch
        {
            VerdictLevel.Ok      => "✓",
            VerdictLevel.Info    => "i",
            VerdictLevel.Unknown => "?",
            _                    => "!",
        };
        _icon.ToolTip = verdict.LevelText;
        if (verdict.Level is VerdictLevel.Warning or VerdictLevel.Critical)
            BorderBrush = brush;
        else
            SetResourceReference(BorderBrushProperty, "Border");

        _headline.Text = verdict.Headline;
        _why.Text      = verdict.Why ?? string.Empty;
        AutomationProperties.SetName(this, $"Verdict, {verdict.LevelText}: {verdict.Headline}");

        // The same actions as before (a live page's verdict is shown again on every refresh): the
        // buttons stay, so the keyboard isn't taken from one.
        if (!_shownActions.SequenceEqual(verdict.Actions))
            ShowActions(verdict.Actions);
        ShowFold();
    }

    private IReadOnlyList<VerdictAction> _shownActions = [];

    private void ShowActions(IReadOnlyList<VerdictAction> actions)
    {
        _shownActions = actions;
        _actions.Children.Clear();
        foreach (var action in actions)
        {
            var button = new Button { Content = action.Text, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(0, 0, 8, 0) };
            button.SetResourceReference(StyleProperty, "SecondaryButton");
            button.Click += (_, _) =>
            {
                ActionRequested?.Invoke(action);
                if (Command?.CanExecute(action) == true)
                    Command.Execute(action);
            };
            _actions.Children.Add(button);
        }
    }

    /// <summary>Folds it to the icon and first line, or back; from the remembered folds, not saved.</summary>
    public void Restore(bool folded) => SetFolded(folded, byUser: false);

    private void SetFolded(bool folded, bool byUser)
    {
        if (_folded == folded)
            return;
        _folded = folded;
        ShowFold();
        if (byUser)
            FoldChanged?.Invoke(folded);
    }

    private void ShowFold()
    {
        _fold.IsChecked     = !_folded;
        _chevron.Text       = _folded ? FoldSection.Folded : FoldSection.Unfolded;
        _fold.ToolTip       = _folded ? "Unfold: why, and what to do" : "Fold to one line";
        AutomationProperties.SetName(_fold, _folded ? "Unfold the verdict" : "Fold the verdict");
        var hasWhy          = !string.IsNullOrEmpty(_verdict?.Why);
        _why.Visibility     = _folded || !hasWhy ? Visibility.Collapsed : Visibility.Visible;
        _actions.Visibility = _folded || _actions.Children.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _headline.TextTrimming = _folded ? TextTrimming.CharacterEllipsis : TextTrimming.None;
        _headline.TextWrapping = _folded ? TextWrapping.NoWrap : TextWrapping.Wrap;
    }

    /// <summary>The severity's colour: green, amber, red; grey when it isn't known or only a note.</summary>
    internal static Brush Brush(VerdictLevel level) => level switch
    {
        VerdictLevel.Ok       => HealthBrushes.Green,
        VerdictLevel.Warning  => HealthBrushes.Amber,
        VerdictLevel.Critical => HealthBrushes.Red,
        _                     => HealthBrushes.Grey,
    };

    private static Brush Tint(Brush brush)
    {
        var color = ((SolidColorBrush)brush).Color;
        var tint  = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B));
        tint.Freeze();
        return tint;
    }
}
