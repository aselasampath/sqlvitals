using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// The page template (#146): every page on it is laid out the same way, top to bottom: its title
/// and page actions, a verdict saying whether something is wrong, summary sections that fold to a
/// one-line header, then the detail. The verdict and the sections remember their fold per page
/// in <see cref="Folds"/>. Built in code, like <see cref="WorkspaceShell"/>; a page's own elements
/// keep their names and resources wherever they are placed.
/// </summary>
[ContentProperty(nameof(Detail))]
public sealed class PageShell : Decorator
{
    /// <summary>The key the verdict's fold is remembered by.</summary>
    public const string VerdictKey = "verdict";

    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(PageShell),
            new PropertyMetadata(string.Empty, (d, e) => ((PageShell)d)._title.Text = (string)e.NewValue));

    public static readonly DependencyProperty VerdictProperty =
        DependencyProperty.Register(nameof(Verdict), typeof(PageVerdict), typeof(PageShell),
            new PropertyMetadata(null, (d, e) =>
            {
                var shell = (PageShell)d;
                shell.VerdictView.Show((PageVerdict?)e.NewValue);
                shell.ShowPlaceholder();
            }));

    private readonly TextBlock   _title   = new() { Margin = new Thickness(0), VerticalAlignment = VerticalAlignment.Center, FontSize = 20 };
    // A ContentControl, not a ContentPresenter: the page's buttons are its logical children from the
    // start, so they bind to the page's view model before the page is first laid out (#186).
    private readonly ContentControl _actions = new() { VerticalAlignment = VerticalAlignment.Center, Focusable = false, IsTabStop = false };
    private readonly StackPanel  _sections = new();
    private readonly Border      _detail  = new();
    // Where the verdict will be, until the page has read (#117): "Loading…", or why nothing was read.
    private readonly Border      _placeholder = new()
    {
        CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Padding = new Thickness(14, 12, 14, 12),
        Margin = new Thickness(0, 0, 0, 12), Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock   _placeholderText = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap };
    private ISectionFolds? _folds;
    private string _pageKey = string.Empty;

    public PageShell()
    {
        _title.SetResourceReference(StyleProperty, "SectionHeader");
        _title.FontSize = 20;

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12), LastChildFill = true };
        DockPanel.SetDock(_actions, Dock.Right);
        header.Children.Add(_actions);
        header.Children.Add(_title);

        VerdictView = new VerdictView();
        VerdictView.FoldChanged += folded => Remember(VerdictKey, folded);

        _placeholder.SetResourceReference(Border.BackgroundProperty, "BgCard");
        _placeholder.SetResourceReference(Border.BorderBrushProperty, "Border");
        _placeholderText.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        _placeholder.Child = _placeholderText;

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(VerdictView, 1);
        Grid.SetRow(_placeholder, 1);
        Grid.SetRow(_sections, 2);
        Grid.SetRow(_detail, 3);
        grid.Children.Add(header);
        grid.Children.Add(VerdictView);
        grid.Children.Add(_placeholder);
        grid.Children.Add(_sections);
        grid.Children.Add(_detail);
        Child = grid;

        Sections.CollectionChanged += Sections_CollectionChanged;
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>The page's own buttons, at the right of its title.</summary>
    public object? Actions
    {
        get => _actions.Content;
        set => _actions.Content = value;
    }

    /// <summary>What the page says first; none hides the verdict.</summary>
    public PageVerdict? Verdict
    {
        get => (PageVerdict?)GetValue(VerdictProperty);
        set => SetValue(VerdictProperty, value);
    }

    /// <summary>Run with the <see cref="VerdictAction"/> when one of the verdict's actions is pressed.</summary>
    public ICommand? VerdictCommand
    {
        get => VerdictView.Command;
        set => VerdictView.Command = value;
    }

    /// <summary>The summary sections, between the verdict and the detail.</summary>
    public ObservableCollection<FoldSection> Sections { get; } = [];

    /// <summary>The page's detail: its grids, map or tabs. It takes the height left.</summary>
    public UIElement? Detail
    {
        get => _detail.Child;
        set => _detail.Child = value;
    }

    /// <summary>The page the folds are remembered under: its tag in <see cref="NavMenu"/>.</summary>
    public string PageKey
    {
        get => _pageKey;
        set { _pageKey = value; RestoreFolds(); }
    }

    /// <summary>Where the folds are remembered; none, and they last as long as the page.</summary>
    public ISectionFolds? Folds
    {
        get => _folds;
        set { _folds = value; RestoreFolds(); }
    }

    public VerdictView VerdictView { get; }

    /// <summary>What the verdict's place says before the page has read; empty once it has, or once it has a verdict.</summary>
    internal string PlaceholderText => _placeholder.Visibility == Visibility.Visible ? _placeholderText.Text : string.Empty;

    // Before the page has read anything (#117): there is no verdict yet, so its place says why.
    internal void ShowPlaceholder()
    {
        var text = Verdict is null ? LoadingPlaceholder.VerdictText(LoadingPlaceholder.GetState(this)) : null;
        _placeholderText.Text   = text ?? string.Empty;
        _placeholder.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Sections_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        foreach (FoldSection section in e.OldItems ?? Array.Empty<FoldSection>())
        {
            section.FoldChanged -= Section_FoldChanged;
            _sections.Children.Remove(section);
        }
        foreach (FoldSection section in e.NewItems ?? Array.Empty<FoldSection>())
        {
            section.FoldChanged += Section_FoldChanged;
            _sections.Children.Add(section);
        }
        RestoreFolds();
    }

    private void Section_FoldChanged(FoldSection section, bool folded) => Remember(section.Key, folded);

    private void Remember(string key, bool folded)
    {
        if (_folds is null || _pageKey.Length == 0 || key.Length == 0)
            return;
        try { _folds.SetFolded(_pageKey, key, folded); }
        catch (Exception ex) { AppLog.Error($"{_pageKey}: could not remember a folded section", ex); }
    }

    // Each section as the user last left it on this page.
    private void RestoreFolds()
    {
        if (_folds is null || _pageKey.Length == 0)
            return;
        IReadOnlySet<string> folded;
        try { folded = _folds.Folded(_pageKey); }
        catch (Exception ex)
        {
            AppLog.Error($"{_pageKey}: could not read its folded sections", ex);
            return;
        }

        VerdictView.Restore(folded.Contains(VerdictKey));
        foreach (var section in Sections)
        {
            section.Restoring = true;
            try { section.IsFolded = folded.Contains(section.Key); }
            finally { section.Restoring = false; }
        }
    }
}
