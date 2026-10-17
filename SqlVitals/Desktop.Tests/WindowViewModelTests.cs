using System.IO;
using System.Windows;
using System.Windows.Controls;
using LiveChartsCore.SkiaSharpView.WPF;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Windows;
using SqlVitals.Desktop.Windows;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.QueryStore;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The SQL script, query plan and Query Store query windows on their view models (#191): what
/// each copies, saves and shows, without a window; then each window built over its view model in
/// both themes, with no binding broken.
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class WindowViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly FakeClipboard _clipboard = new();
    private readonly FakeFileDialogs _files = new();
    private readonly FakeDialogService _dialogs = new();

    public WindowViewModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    // ── The SQL script ───────────────────────────────────────────────────

    private static readonly SqlScript Script = new("Update Statistics", "Check the tables first.", "UPDATE STATISTICS dbo.Orders;", "UpdateStatistics_20261017_0900.sql");

    [Fact]
    public void TheScript_IsCopied_AndSavedWhereTheUserPicks()
    {
        var model = new SqlScriptViewModel(Script, _clipboard, _files, _dialogs);

        model.CopyCommand.Execute(null);
        Assert.Equal(Script.Text, _clipboard.Text);

        // Cancelled: nothing written.
        model.SaveCommand.Execute(null);
        Assert.Equal("UpdateStatistics_20261017_0900.sql", _files.Asked.Single().FileName);

        _files.Path = Path.Combine(_dir, "update.sql");
        model.SaveCommand.Execute(null);
        Assert.Equal(Script.Text, File.ReadAllText(_files.Path));
        Assert.Empty(_dialogs.Shown);

        _files.Path = Path.Combine(_dir, "no such folder", "update.sql");
        model.SaveCommand.Execute(null);
        Assert.StartsWith("Error: Save Failed: Failed to save the file:", Assert.Single(_dialogs.Shown), StringComparison.Ordinal);

        var closed = 0;
        model.CloseRequested += () => closed++;
        model.CloseCommand.Execute(null);
        Assert.Equal(1, closed);
    }

    // ── The query plan ───────────────────────────────────────────────────

    private const string PlanXml =
        """<ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.6"><BatchSequence><Batch><Statements><StmtSimple StatementText="SELECT 1" StatementId="1" StatementType="SELECT" /></Statements></Batch></BatchSequence></ShowPlanXML>""";

    [Fact]
    public void WithoutAPlan_ItSaysSo_AndThereIsNothingToCopySaveOrExport()
    {
        var model = new QueryPlanViewModel(null, "  SELECT 1  ", null, _clipboard, _files, _dialogs);

        Assert.Equal("Query Execution Plan", model.Title);
        Assert.Equal("SELECT 1", model.QueryText);
        Assert.False(model.HasPlanXml);
        Assert.Equal($"({QueryPlanViewModel.NoPlanMessage})", model.XmlText);
        Assert.False(model.CopyCommand.CanExecute(null));
        Assert.False(model.SaveCommand.CanExecute(null));
        Assert.False(model.ExportDiagramCommand.CanExecute(null));
    }

    [Fact]
    public void ThePlansXml_IsFormattedTheFirstTimeItsTabShows_AndCopiedAndSavedAsItCame()
    {
        var model = new QueryPlanViewModel(PlanXml, "SELECT 1", "Execution plan · query 42", _clipboard, _files, _dialogs);
        Assert.Equal("Execution plan · query 42", model.Title);
        Assert.Equal(string.Empty, model.XmlText);

        model.SelectedTab = QueryPlanViewModel.XmlTab;
        Assert.Contains(Environment.NewLine + "  <BatchSequence>", model.XmlText, StringComparison.Ordinal);

        model.CopyCommand.Execute(null);
        Assert.Equal(PlanXml, _clipboard.Text);

        _files.Path = Path.Combine(_dir, "plan.sqlplan");
        model.SaveCommand.Execute(null);
        Assert.Equal(PlanXml, File.ReadAllText(_files.Path));
        Assert.StartsWith("Inform: Saved: Plan saved successfully.", Assert.Single(_dialogs.Shown), StringComparison.Ordinal);
    }

    [Fact]
    public void ExportingTheDiagram_ShowsItsTab_AndTheWindowRendersIt_AFailureSaysWhy()
    {
        var model = new QueryPlanViewModel(PlanXml, null, null, _clipboard, _files, _dialogs) { SelectedTab = QueryPlanViewModel.XmlTab };
        var rendered = new List<(string Path, int Tab)>();
        model.RenderDiagram = path => rendered.Add((path, model.SelectedTab));
        Assert.False(model.ExportDiagramCommand.CanExecute(null));

        model.HasDiagram = true;
        Assert.True(model.ExportDiagramCommand.CanExecute(null));
        _files.Path = Path.Combine(_dir, "plan.png");
        model.ExportDiagramCommand.Execute(null);
        Assert.Equal([(_files.Path, QueryPlanViewModel.GraphicalTab)], rendered);

        model.RenderDiagram = _ => throw new IOException("The disk is full.");
        model.ExportDiagramCommand.Execute(null);
        Assert.Equal("Error: Export Failed: Failed to export the diagram:\nThe disk is full.", Assert.Single(_dialogs.Shown));
    }

    // ── The Query Store query ────────────────────────────────────────────

    private static readonly DateTime T0 = DateTime.UtcNow.AddHours(-3);

    private static QueryStorePlanInterval Interval(int hour, long plan, long executions, double durationUs) =>
        new(T0.AddHours(hour), plan, 0, QueryStoreTotals.Zero with { Executions = executions, DurationUs = durationUs }, null, null);

    private static QueryStoreQueryDetails Details(params QueryStorePlanInterval[] intervals) =>
        new(new QueryStoreQueryInfo(42, 0, null, "  SELECT * FROM dbo.Orders  "), intervals,
            [new QueryStorePlanState(7, IsForced: true, 0, null), new QueryStorePlanState(9, IsForced: false, 0, null)]);

    private QueryStoreQueryViewModel Query(Func<long, QueryStorePeriod, Task<QueryStoreQueryDetails>> read) =>
        new(new FakeRepository { GetQueryStoreQueryDetailsAsync = read }, 42, QueryStorePeriod.Last24Hours,
            new QueryStoreStatus("SalesDb", null, QueryStoreFeatures.None), _dialogs, _clipboard);

    [Fact]
    public async Task TheQuery_ChartsAveragePerPlan_TheForcedPlanEmphasised_AndTheMetricCanChange()
    {
        var model = Query((_, _) => Task.FromResult(Details(Interval(0, 7, 10, 20_000), Interval(1, 7, 10, 40_000), Interval(1, 9, 4, 4_000))));
        Assert.Equal("Query 42 · Query Store · SalesDb", model.Title);
        Assert.Equal(QueryStoreMetric.Duration, model.Metric);
        Assert.DoesNotContain(model.Metrics, m => m.Value == QueryStoreMetric.LogUsed);   // 2016's Query Store doesn't keep it

        await model.LoadAsync();

        Assert.Equal("SELECT * FROM dbo.Orders", model.QueryText);
        Assert.Equal("2 plans used in last 24 hours · one point per Query Store interval", model.Status);
        Assert.Null(model.NoData);
        Assert.Equal(["Plan 7 (forced)", "Plan 9"], model.Chart.Lines.Select(l => l.Name));
        Assert.Equal([true, false], model.Chart.Lines.Select(l => l.Emphasised));
        Assert.Equal([2.0, 4.0], model.Chart.Lines[0].Points.Select(p => p.Value));   // ms per execution
        Assert.Equal("Avg Duration ms", model.Chart.ValueName);
        Assert.Equal(2, model.Plans.Count);

        model.Metric = QueryStoreMetric.Executions;
        Assert.Equal("Executions", model.Chart.ValueName);
        Assert.Equal([10.0, 10.0], model.Chart.Lines[0].Points.Select(p => p.Value));

        model.CopyTextCommand.Execute(null);
        Assert.Equal("  SELECT * FROM dbo.Orders  ", _clipboard.Text);
    }

    [Fact]
    public async Task AQueryThatDidntRun_SaysSo_OneQueryStoreHasNot_SaysWhy_AndAFailureGoesToTheStatus()
    {
        var quiet = Query((_, _) => Task.FromResult(Details()));
        await quiet.LoadAsync();
        Assert.Equal("The query didn't run in last 24 hours.", quiet.NoData);
        Assert.Empty(quiet.Chart.Lines);

        var gone = Query((_, _) => Task.FromResult(new QueryStoreQueryDetails(null, [], [])));
        await gone.LoadAsync();
        Assert.StartsWith("Query Store for SalesDb has no query 42.", gone.Subheading, StringComparison.Ordinal);
        Assert.False(gone.CopyTextCommand.CanExecute(null));

        var failed = Query((_, _) => Task.FromException<QueryStoreQueryDetails>(new InvalidOperationException("Timeout expired.")));
        await failed.LoadAsync();
        Assert.Contains("Timeout expired.", failed.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewPlan_OpensThePlanWindow_WithTheQuerysText()
    {
        var repo = new FakeRepository
        {
            GetQueryStoreQueryDetailsAsync = (_, _) => Task.FromResult(Details(Interval(0, 7, 1, 1))),
            GetQueryStorePlanAsync         = _ => Task.FromResult<string?>(PlanXml),
        };
        var model = new QueryStoreQueryViewModel(repo, 42, QueryStorePeriod.Last24Hours, new QueryStoreStatus("SalesDb", null, QueryStoreFeatures.None),
                                                 _dialogs, _clipboard);

        model.ViewPlanCommand.Execute(new QueryStorePlanSummary(7, null, null, 1, 1, 1, 1, 0, true, 0, null));

        Assert.Equal("Plan: Execution plan · Query Store plan 7 · query 42", Assert.Single(_dialogs.Shown));
    }

    // ── The windows over them ────────────────────────────────────────────

    [Theory, MemberData(nameof(Themes))]
    public void EachWindow_BindsItsViewModel_WithNoBrokenBinding(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        using var errors = BindingErrors.Watch();

        var script = new SqlScriptWindow(new SqlScriptViewModel(Script, _clipboard, _files, _dialogs));
        Lay(script);
        Assert.Equal("Update Statistics", script.Title);
        Assert.Contains(UiThread.Find<TextBox>((DependencyObject)script.Content), t => t.Text == Script.Text);

        var plan = new QueryExecutionPlanWindow(new QueryPlanViewModel(null, "SELECT 1", "Execution plan · query 42", _clipboard, _files, _dialogs));
        Lay(plan);
        Assert.Equal("Execution plan · query 42", plan.Title);
        var planModel = (QueryPlanViewModel)plan.DataContext;
        Assert.False(planModel.HasDiagram);
        // The XML tab, chosen by the view model: what's there says there is no plan.
        planModel.SelectedTab = QueryPlanViewModel.XmlTab;
        Lay(plan);
        Assert.Contains(UiThread.Find<TextBox>((DependencyObject)plan.Content), t => t.Text == $"({QueryPlanViewModel.NoPlanMessage})");
        Assert.Contains(UiThread.Find<TextBlock>((DependencyObject)plan.Content), t => t.Text == " — plan not available in cache" && t.Visibility == Visibility.Visible);

        var model = Query((_, _) => Task.FromResult(Details(Interval(0, 7, 10, 20_000), Interval(1, 9, 4, 4_000))));
        var query = new QueryStoreQueryWindow(model);
        UiThread.Wait(model.LoadAsync());
        Lay(query);
        Assert.Equal("Query 42 · Query Store · SalesDb", query.Title);
        Assert.Equal(2, UiThread.Find<CartesianChart>((DependencyObject)query.Content).Single().Series.Count());
        Assert.Equal(2, UiThread.Find<DataGrid>((DependencyObject)query.Content).Single().Items.Count);
        Assert.Equal("Avg Duration ms", UiThread.Find<ComboBox>((DependencyObject)query.Content).Single().Text);

        errors.AssertNone();
        foreach (var window in new Window[] { script, plan, query })
            window.Close();
    });

    // Laid out off screen, as the window would be.
    private static void Lay(Window window) => UiThread.Layout((FrameworkElement)window.Content, 1180, 820);
}
