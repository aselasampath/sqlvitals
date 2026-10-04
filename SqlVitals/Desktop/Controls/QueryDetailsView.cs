using System.Windows;
using System.Windows.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.QueryStore;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// A Query Store query's details in the drawer (#147): its figures from the tab it was selected
/// on, its text, and its plans in the period, each of which opens in a window. Query details,
/// with its history, opens in its own window too.
/// </summary>
public sealed class QueryDetailsView : StackPanel
{
    private readonly Func<long, Task<IReadOnlyList<QueryStorePlanSummary>>> _loadPlans;
    private readonly Func<Button, long, long, string, Task> _openPlan;
    private readonly Action<long> _openQuery;

    private readonly Grid      _rows  = DrawerParts.Rows();
    private readonly TextBox   _text  = DrawerParts.Code(maxHeight: 260);
    private readonly TextBlock _plansNote = DrawerParts.Note();
    private readonly TextBlock _plansHeading = DrawerParts.Heading("Plans");
    private readonly StackPanel _plans = new();

    private long _queryId;
    private string _queryText = string.Empty;
    private string? _plansPeriod;
    private int _plansVersion;

    /// <param name="loadPlans">Reads a query's plans in the page's period.</param>
    /// <param name="openPlan">Opens a plan in a window: the button pressed, the plan, the query, its text.</param>
    /// <param name="openQuery">Opens the Query details window.</param>
    public QueryDetailsView(Func<long, Task<IReadOnlyList<QueryStorePlanSummary>>> loadPlans,
                            Func<Button, long, long, string, Task> openPlan, Action<long> openQuery)
    {
        _loadPlans = loadPlans;
        _openPlan  = openPlan;
        _openQuery = openQuery;

        Children.Add(_rows);
        Children.Add(DrawerParts.Row(
            DrawerParts.Button("Query details in a window", () => _openQuery(_queryId),
                               "Its history and every plan, in its own window, to keep beside the grid or on another monitor")));

        Children.Add(DrawerParts.Heading("Query text"));
        Children.Add(_text);
        Children.Add(DrawerParts.Row(DrawerParts.Button("Copy", () => ClipboardHelper.SetText(_queryText), "Copy the query text")));

        Children.Add(_plansHeading);
        Children.Add(_plansNote);
        Children.Add(_plans);
    }

    public long QueryId => _queryId;

    /// <summary>The figures as shown, "Label: value"; for tests.</summary>
    internal IReadOnlyList<string> RowTexts => DrawerParts.Texts(_rows);

    /// <summary>Each plan's title and line; for tests.</summary>
    internal IReadOnlyList<string> PlanTexts =>
        _plans.Children.OfType<DockPanel>().Select(p => string.Join(" | ", p.Children.OfType<StackPanel>().SelectMany(s => s.Children.OfType<TextBlock>()).Select(t => t.Text))).ToList();

    internal string PlansNote => _plansNote.Text;

    /// <summary>
    /// Shows a row's query, with its figures for <paramref name="period"/>. Its plans are read
    /// again for another query or another period.
    /// </summary>
    public Task ShowAsync(object row, string period)
    {
        if (QueryStoreDetails.QueryOf(row) is not { } query)
            return Task.CompletedTask;
        var reload = query.QueryId != _queryId || period != _plansPeriod;
        _queryId   = query.QueryId;
        _queryText = query.QueryText;

        DrawerParts.Fill(_rows, QueryStoreDetails.Rows(row, period));
        _text.Text = query.QueryText;
        return reload ? LoadPlansAsync(period) : Task.CompletedTask;
    }

    private async Task LoadPlansAsync(string period)
    {
        var version = ++_plansVersion;
        var queryId = _queryId;
        _plansPeriod       = period;
        _plansHeading.Text = $"Plans · {period}";
        _plans.Children.Clear();
        _plansNote.Text       = "Reading its plans…";
        _plansNote.Visibility = Visibility.Visible;
        try
        {
            var plans = await _loadPlans(queryId);
            if (version != _plansVersion)
                return;
            _plansNote.Text = plans.Count switch
            {
                0     => "None of its plans ran in this period.",
                1     => "1 plan ran in this period.",
                var n => $"{n} plans ran in this period: compare them in Query details.",
            };
            foreach (var plan in plans)
                _plans.Children.Add(PlanRow(plan));
        }
        catch (Exception ex)
        {
            if (version == _plansVersion)
                _plansNote.Text = "Its plans couldn't be read: " + MainWindow.FormatErrorStatus(ex);
        }
    }

    // "Plan 902 · forced", its figures, and Open in window.
    private DockPanel PlanRow(QueryStorePlanSummary plan)
    {
        var title = new TextBlock { Text = QueryStoreDetails.PlanTitle(plan), FontSize = 12, FontWeight = FontWeights.SemiBold };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
        var line = new TextBlock { Text = QueryStoreDetails.PlanLine(plan), FontSize = 11.5, TextWrapping = TextWrapping.Wrap };
        line.SetResourceReference(TextBlock.ForegroundProperty, "TextMuted");
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        text.Children.Add(line);

        Button? open = null;
        open = DrawerParts.Button("Open in window", async () => await _openPlan(open!, plan.PlanId, _queryId, _queryText),
                                  $"Plan {plan.PlanId}, drawn like SSMS, in its own window");
        open.Margin = new Thickness(8, 0, 0, 0);
        open.VerticalAlignment = VerticalAlignment.Center;

        var row = new DockPanel { Margin = new Thickness(0, 4, 0, 4) };
        DockPanel.SetDock(open, Dock.Right);
        row.Children.Add(open);
        row.Children.Add(text);
        return row;
    }
}
