using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.ExecutionPlans;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.TableStatistics;
using static SqlVitals.Desktop.Tests.TableStatisticsDetailsTests;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The links to Table Statistics on screen (#244), in both themes: the page opened on a table, what it
/// says when it can't, the state a login that can't read statistics sees, and the plan view's button.
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class TableStatisticsLinkViewTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private (TableStatisticsPage Page, TableStatisticsViewModel Model) Build(FakeRepository repo)
    {
        var model = new TableStatisticsViewModel(repo, new FakeNavigator(), new FakeDialogService());
        return (new TableStatisticsPage(model, new ConnectionSettingsService(_dir)), model);
    }

    private static PageFocus Focus(TableStatisticsTarget target) =>
        new(new FocusFilter(FocusKind.Table, Table: target), new FocusOrigin(FocusSource.Page, target.FullName, "Stale Statistics"));

    [Theory, MemberData(nameof(Themes))]
    public void OpenedOnAnObject_SelectsTheRow_AndShowsTheStatisticsTab(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        using var errors = BindingErrors.Watch();
        var (page, model) = Build(Repository());
        ((IFocusPage)page).ShowFocus(Focus(new TableStatisticsTarget("SalesDb", "dbo", "Lines", "IX_Orders_Status")));

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            UiThread.Wait(Task.Delay(150));
            page.UpdateLayout();

            Assert.Equal("Lines", ((ViewModels.TableSizeRow)page.TablesGrid.SelectedItem).TableName);
            Assert.Equal(TableStatisticsViewModel.StatisticsTab, model.DetailTab);
            Assert.Equal("IX_Orders_Status", model.Statistics.SelectedObject?.Name);
            Assert.False(page.TxtTargetProblem.IsVisible);
        });
        model.OnNavigatedFrom();
        errors.AssertNone();
    });

    [Theory, MemberData(nameof(Themes))]
    public void ATableNotFound_IsSaidOnThePage(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        using var errors = BindingErrors.Watch();
        var (page, model) = Build(Repository());
        ((IFocusPage)page).ShowFocus(Focus(new TableStatisticsTarget("SalesDb", "dbo", "Dropped")));

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            UiThread.Wait(Task.Delay(100));
            page.UpdateLayout();

            Assert.True(page.TxtTargetProblem.IsVisible);
            Assert.Contains("dbo.Dropped", page.TxtTargetProblem.Text);
            Assert.Equal(2, page.TablesGrid.Items.Count);
        });
        model.OnNavigatedFrom();
        errors.AssertNone();
    });

    [Theory, MemberData(nameof(Themes))]
    public void ALoginThatCantRead_SeesWhyAndNoFigures(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        using var errors = BindingErrors.Watch();
        var denied = new TableReadFailure(TableReadFailure.Statistics, "The login lacks SELECT in SalesDb. " + TableReadFailure.SelectWarning,
                                          "USE [SalesDb];\nGRANT SELECT ON [dbo].[Orders] TO [svc];");
        var (page, model) = Build(Repository(new StatisticsDetails(null, [], [], 160, false, [denied])));
        ((IFocusPage)page).ShowFocus(Focus(new TableStatisticsTarget("SalesDb", "dbo", "Orders", "PK_Orders")));

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            UiThread.Wait(Task.Delay(150));
            page.UpdateLayout();
            TemplateRolloutTests.Render(page, $"table-statistics-denied-{theme}.png");

            var panel = UiThread.Find<TableStatisticsDetailsPanel>(page).Single();
            Assert.True(panel.TxtDenied.IsVisible);
            Assert.True(panel.TxtProblem.IsVisible);
            Assert.Contains("read the table's rows", panel.TxtProblem.Text);
            Assert.False(panel.HeaderFigures.IsVisible);
            Assert.False(panel.BtnScript.IsEnabled);
            Assert.False(panel.ChkFullScan.IsEnabled);
            Assert.True(panel.CmbObject.IsEnabled);
        });
        model.OnNavigatedFrom();
        errors.AssertNone();
    });

    [Fact]
    public void TheLinks_AreOnTheirPages() => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed("DarkTheme");
        var repo = new FakeRepository();
        var settings = new ConnectionSettingsService(_dir);

        var stale = new StaleStatisticsPage(new StaleStatisticsViewModel(repo, new FakeDialogService(), new FakeNavigator()), settings);
        Assert.Same(stale.ViewModel.OpenTableStatisticsCommand, stale.BtnTableStatistics.Command);

        var index = new IndexHealthPage(new IndexHealthViewModel(repo, repo, new FakeDialogService(), new FakeNavigator()), settings);
        Assert.Same(index.ViewModel.OpenUnusedTableCommand, index.BtnUnusedTable.Command);
        Assert.Same(index.ViewModel.OpenFragmentedTableCommand, index.BtnFragTable.Command);
    });

    [Theory, MemberData(nameof(Themes))]
    public void ThePlanView_OffersTheLinkOnlyOnAnOperatorWithATable(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        var plan = ShowplanParser.Parse("""
            <ShowPlanXML xmlns="http://schemas.microsoft.com/sqlserver/2004/07/showplan" Version="1.5">
              <BatchSequence><Batch><Statements>
                <StmtSimple StatementId="1" StatementType="SELECT" StatementSubTreeCost="1">
                  <QueryPlan>
                    <RelOp NodeId="0" PhysicalOp="Nested Loops" LogicalOp="Inner Join" EstimateRows="1" EstimatedTotalSubtreeCost="1">
                      <OutputList/>
                      <NestedLoops Optimized="0">
                        <RelOp NodeId="1" PhysicalOp="Index Seek" LogicalOp="Index Seek" EstimateRows="1" EstimatedTotalSubtreeCost="0.5">
                          <OutputList/>
                          <RunTimeInformation><RunTimeCountersPerThread Thread="0" ActualRows="5000" ActualExecutions="1"/></RunTimeInformation>
                          <IndexScan><Object Database="[SalesDb]" Schema="[dbo]" Table="[Orders]" Index="[PK_Orders]"/></IndexScan>
                        </RelOp>
                      </NestedLoops>
                    </RelOp>
                  </QueryPlan>
                </StmtSimple>
              </Statements></Batch></BatchSequence>
            </ShowPlanXML>
            """);
        var join = plan.Statements[0].Root.Children[0];
        var seek = join.Children[0];

        var view = new ExecutionPlanView();
        view.Show(plan, "none");
        TableStatisticsTarget? opened = null;
        view.OpenTableRequested += target => opened = target;

        Assert.Equal(Visibility.Collapsed, view.BtnTableStatistics.Visibility);

        view.SelectOperator(join);
        Assert.Equal(Visibility.Collapsed, view.BtnTableStatistics.Visibility);

        view.SelectOperator(seek);
        Assert.Equal(Visibility.Visible, view.BtnTableStatistics.Visibility);
        Assert.Equal("Bad estimate: open Table Statistics", view.BtnTableStatistics.Content);

        view.BtnTableStatistics.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(new TableStatisticsTarget("SalesDb", "dbo", "Orders"), opened);
    });
}
