using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.ExecutionPlans;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// A plan the drawer can show for a row (#149): read when asked, drawn inline, and opened in its
/// own window as the page's plan button always did.
/// </summary>
/// <param name="Heading">"Execution plan", "Plan used most now".</param>
/// <param name="Load">Reads the plan's XML; null when there is none.</param>
/// <param name="WindowTitle">The plan window's title.</param>
public sealed record DrawerPlan(string Heading, Func<Task<string?>> Load, string WindowTitle);

/// <summary>
/// What the details drawer shows for a page whose selected row goes in a <see cref="RowDetailsView"/>
/// (#150): the row, or the hint while none is selected. A page that is an <see cref="IDetailsSource"/>
/// hands its <c>Details</c> and <c>DetailsChanged</c> to one of these.
/// </summary>
/// <param name="title">The drawer's title while nothing is selected.</param>
/// <param name="hint">What to select for details.</param>
internal sealed class RowDrawer(string title, string hint)
{
    public DrawerContent Content { get; private set; } = new(title, null, hint);

    /// <summary>The view, once a row has been shown; for tests.</summary>
    public RowDetailsView? View { get; private set; }

    /// <summary>The content changed; <c>true</c> when the user asked to see it, so a closed drawer opens.</summary>
    public event Action<bool>? Changed;

    /// <summary>Shows a row: its figures, and a text (a script, a message) with Copy when it has one.</summary>
    /// <param name="noText">Shown when there is no text; null hides the text and its heading.</param>
    public void Show(string rowTitle, IEnumerable<DetailRow> rows, bool open, string textHeading = "", string? text = null, string? noText = null)
    {
        View ??= new RowDetailsView();
        View.Show(rows, textHeading, text, [], noText);
        Content = new(rowTitle, View, hint);
        Changed?.Invoke(open);
    }

    /// <summary>No row is selected any more (a filter hid it): the hint again.</summary>
    public void Clear()
    {
        if (Content.Body is null)
            return;
        Content = new(title, null, hint);
        Changed?.Invoke(false);
    }
}

/// <summary>
/// A grid row's details in the drawer (#149), for Resource Queries, Query Regressions and SP Trace:
/// its figures, its text with Copy, and its plans, each shown inline or in a window. The Resources,
/// Maintenance, Configuration and Alerts pages use it too (#150): a fix script or a job's message
/// as the text, and no plans.
/// </summary>
public sealed class RowDetailsView : StackPanel
{
    private readonly Grid       _rows        = DrawerParts.Rows();
    private readonly TextBlock  _textHeading = DrawerParts.Heading("Query text");
    private readonly TextBox    _text        = DrawerParts.Code(maxHeight: 260);
    private readonly WrapPanel  _copy;
    private readonly StackPanel _plans       = new();
    private readonly List<PlanPart> _parts   = [];
    private string? _queryText;

    public RowDetailsView()
    {
        Children.Add(_rows);
        Children.Add(_textHeading);
        Children.Add(_text);
        _copy = DrawerParts.Row(DrawerParts.Button("Copy", () => ClipboardHelper.SetText(_queryText ?? string.Empty), "Copy the text"));
        Children.Add(_copy);
        Children.Add(_plans);
    }

    /// <summary>The rows as shown, "Label: value"; for tests.</summary>
    internal IReadOnlyList<string> RowTexts => DrawerParts.Texts(_rows);

    internal string Text => _text.Text;

    /// <summary>Whether the text, its heading and Copy are shown; for tests.</summary>
    internal bool TextShown => _text.Visibility == Visibility.Visible;

    /// <summary>Each plan's heading; for tests.</summary>
    internal IReadOnlyList<string> PlanHeadings => _parts.Select(p => p.Heading.Text).ToList();

    /// <summary>Reads and draws a plan inline, as its Show the plan button does; for tests.</summary>
    internal Task ShowPlanAsync(int index) => _parts[index].ShowAsync();

    internal bool PlanShown(int index) => _parts[index].View.Visibility == Visibility.Visible && _parts[index].View.HasDiagram;

    /// <summary>Shows a row: its figures, its text and its plans, none read until asked for.</summary>
    /// <param name="textHeading">"Query text", "Statement", "To fix it".</param>
    /// <param name="noText">Shown when there is no text; null hides the text, its heading and Copy instead (#150).</param>
    public void Show(IEnumerable<DetailRow> rows, string textHeading, string? text, IReadOnlyList<DrawerPlan> plans,
                     string? noText = "(no text was captured)")
    {
        DrawerParts.Fill(_rows, rows);
        _queryText        = text;
        _textHeading.Text = textHeading;
        _text.Text        = string.IsNullOrWhiteSpace(text) ? noText ?? string.Empty : text;

        var shown = !string.IsNullOrWhiteSpace(text) || noText is not null ? Visibility.Visible : Visibility.Collapsed;
        _textHeading.Visibility = _text.Visibility = shown;
        _copy.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;

        foreach (var part in _parts)
            part.Forget();
        _parts.Clear();
        _plans.Children.Clear();
        foreach (var plan in plans)
        {
            var part = new PlanPart(this, plan);
            _parts.Add(part);
            _plans.Children.Add(part.Panel);
        }
    }

    // One plan: its heading, Show the plan and Open in window, a line about it, and the diagram.
    private sealed class PlanPart
    {
        private readonly RowDetailsView _owner;
        private readonly DrawerPlan _plan;
        private readonly Button _show;
        private readonly Button _window;
        private readonly TextBlock _note = DrawerParts.Note();
        private string? _xml;
        private bool _loaded;
        private int _version;

        public PlanPart(RowDetailsView owner, DrawerPlan plan)
        {
            _owner  = owner;
            _plan   = plan;
            Heading = DrawerParts.Heading(plan.Heading);
            _show   = DrawerParts.Button("Show the plan", async () => await ShowAsync(), "Read the plan and draw it here");
            _window = DrawerParts.Button("Open in window", async () => await OpenWindowAsync(),
                                         "Open the plan in its own window, to keep it beside the grid or on another monitor");
            Panel.Children.Add(Heading);
            Panel.Children.Add(DrawerParts.Row(_show, _window));
            Panel.Children.Add(_note);
            Panel.Children.Add(View);
        }

        public StackPanel Panel { get; } = new();

        public TextBlock Heading { get; }

        public ExecutionPlanView View { get; } = new() { Height = 300, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };

        // Another row is shown: a read still running is dropped.
        public void Forget() => _version++;

        public async Task ShowAsync()
        {
            if (!await LoadAsync())
                return;
            View.Show(ShowplanParser.Parse(_xml), "No plan is cached for it any more.");
            View.Visibility = _xml is null ? Visibility.Collapsed : Visibility.Visible;
            _show.Content   = "Read the plan again";
        }

        // As the page's plan button did: read, then the plan window, with what to say if there is none.
        private async Task OpenWindowAsync()
        {
            if (!_loaded && !await LoadAsync())
                return;
            CompositionRoot.Get<IDialogService>().ShowQueryPlan(_xml, _owner._queryText, _plan.WindowTitle);
        }

        private async Task<bool> LoadAsync()
        {
            var version = ++_version;
            _show.IsEnabled = _window.IsEnabled = false;
            _owner.Cursor = Cursors.Wait;
            try
            {
                var xml = await _plan.Load();
                if (version != _version)
                    return false;
                (_xml, _loaded) = (xml, true);
                _note.Text       = xml is null ? "No plan is cached for it: it has left the cache, or it was never kept." : string.Empty;
                _note.Visibility = xml is null ? Visibility.Visible : Visibility.Collapsed;
                return true;
            }
            catch (Exception ex)
            {
                if (version == _version)
                {
                    _note.Text       = "The plan couldn't be read: " + MainWindow.FormatErrorStatus(ex);
                    _note.Visibility = Visibility.Visible;
                }
                return false;
            }
            finally
            {
                _owner.Cursor = null;
                if (version == _version)
                    _show.IsEnabled = _window.IsEnabled = true;
            }
        }
    }
}
