using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Deadlocks;
using SqlVitals.Engine.FileIo;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using static SqlVitals.Desktop.Tests.TemplateEverywhereTests;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The third wave of #180 (#187), Deadlocks, Alerts, Memory, Index Health, File I/O and Query
/// Regressions, bound to their view models and rendered whole in both themes (to
/// %TEMP%\SqlVitalsTests\page-template-rollout\&lt;page&gt;-&lt;theme&gt;.png). Each fails on any binding
/// error, and checks the view code each page kept: the drawer, a row's buttons, a double-click, a
/// script button that says what it covers. What their view models decide is tested without a
/// page, in their own tests.
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class Wave3PagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private ConnectionSettingsService Settings() => new(_dir);

    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    // Renders the page, its view model loading it, and fails on any binding error. The test puts
    // the theme on before it builds the page: its StaticResource brushes are looked up then.
    private static void Rendered(string theme, Page page, Func<Task> load, string name, Action whileShown)
    {
        using var errors = BindingErrors.Watch();
        TemplateRolloutTests.Hosted(page, load, $"{name}-{theme}.png", whileShown);
        errors.AssertNone();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                yield return match;
            foreach (var deeper in Descendants<T>(child))
                yield return deeper;
        }
    }

    // A double-click on a grid's row, as the mouse makes it.
    private static void DoubleClick(DataGrid grid, int row)
    {
        var container = (DataGridRow)grid.ItemContainerGenerator.ContainerFromIndex(row);
        grid.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = Control.MouseDoubleClickEvent,
            Source      = Descendants<DataGridCell>(container).First(),
        });
    }

    // ── Deadlocks ────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(Themes))]
    public void Deadlocks_Renders_AndAPatternOpensItsNewestDeadlock(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        var clipboard = new FakeClipboard();
        var page = Wave3Pages.Deadlocks(new FakeRepository { GetDeadlockHistoryAsync = () => Task.FromResult(TemplateRolloutTests.DeadlockHistoryOf(DateTime.UtcNow)) },
                                        Settings(), clipboard: clipboard);
        Rendered(theme, page, page.ViewModel.OnNavigatedToAsync, "deadlocks-vm", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.Tabs);
            Assert.Equal("5 deadlocks", page.TxtCount.Text);
            Assert.Equal(5, page.DeadlocksGrid.Items.Count);
            Assert.Equal(2, page.ProcessesGrid.Items.Count);
            Assert.StartsWith("-- SPID 55 (victim)", page.TxtStatement.Text);
            Assert.True(page.DetailPanel.IsEnabled);
            Assert.Equal(Visibility.Collapsed, page.TxtProblem.Visibility);

            TemplateRolloutTests.Click(page.BtnCopyXml);
            Assert.Equal("<deadlock><victim-list /></deadlock>", clipboard.Text);

            // The Recurring tab: a row's Open newest, and a double-click, open the deadlock on the first tab.
            page.Tabs.SelectedItem = page.TabRecurring;
            page.UpdateLayout();
            var openNewest = Descendants<Button>(page.PatternsGrid).First(b => (string)b.Content == "Open newest");
            TemplateRolloutTests.Click(openNewest);
            Assert.Same(page.TabDeadlocks, page.Tabs.SelectedItem);
            Assert.Same(((DeadlockPattern)page.PatternsGrid.Items[0]).Newest.Xml, ((DeadlockReport)page.DeadlocksGrid.SelectedItem).Xml);

            page.Tabs.SelectedItem = page.TabRecurring;
            page.UpdateLayout();
            page.PatternsGrid.SelectedIndex = 1;
            DoubleClick(page.PatternsGrid, 1);
            Assert.Same(page.TabDeadlocks, page.Tabs.SelectedItem);
            Assert.Equal(((DeadlockPattern)page.PatternsGrid.Items[1]).Newest.TimestampUtc, ((DeadlockReport)page.DeadlocksGrid.SelectedItem).TimestampUtc);

            // The range filters what was read.
            page.CmbRange.SelectedIndex = 0;   // Last hour
            Assert.Equal("1 deadlock", page.TxtCount.Text);
        });
    });

    // ── Alerts ───────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(Themes))]
    public void Alerts_Renders_AndADoubleClickInvestigates(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        var settings = Settings();
        var hot = new ConnectionSettings { Server = "hot", Name = "Hot", Database = "Sales", Authentication = SqlAuthMode.Windows };
        settings.Upsert(hot);
        var feed = new FakeAlertFeed();
        var alert = new Alert(Guid.NewGuid(), hot.Id, HealthIndicator.Cpu, HealthLevel.Critical, DateTime.UtcNow.AddMinutes(-5), null, 99, 90, "CPU at 99 %");
        feed.Active.Add(alert);
        feed.Stored.Add(new(new Alert(Guid.NewGuid(), hot.Id, HealthIndicator.Cpu, HealthLevel.Warning, DateTime.UtcNow.AddHours(-3),
                                      DateTime.UtcNow.AddHours(-2), 91, 90, "CPU at 91 %", AlertEndReason.Recovered), "Hot"));
        var navigator = new FakeNavigator();
        var page = new AlertsPage(new AlertsViewModel(feed, settings, navigator), settings);
        Rendered(theme, page, page.ViewModel.OnNavigatedToAsync, "alerts-vm", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.AlertsGrid);
            Assert.Equal("2 alerts", page.TxtCount.Text);
            Assert.Equal(["All connections", "Hot"], page.CmbConnection.Items.Cast<AlertsViewModel.ConnectionChoice>().Select(c => c.Name));
            Assert.Equal("Active and past", page.CmbStatus.SelectedItem);
            Assert.Equal("Last 7 days", page.CmbPeriod.SelectedItem);
            Assert.StartsWith("An alert starts after ", page.TxtStatus.Text);
            Assert.Equal(Visibility.Collapsed, page.TxtNoData.Visibility);

            page.AlertsGrid.SelectedIndex = 0;
            Assert.Equal("CPU · Hot", page.Details.Title);

            DoubleClick(page.AlertsGrid, 0);
            Assert.Equal((alert, ActivitySources.AlertsPage), Assert.Single(navigator.Investigated));
        });
    });

    // ── Memory ───────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(Themes))]
    public void Memory_Renders_TheFiguresClerksAndGrants(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        var page = Wave3Pages.Memory(MemoryRepo(), Settings());
        Rendered(theme, page, page.ViewModel.OnNavigatedToAsync, "memory-vm", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.Tabs);
            Assert.Equal("24.0 GB", page.KpiTotal.Text);
            Assert.Equal("Available physical memory is high", page.KpiMemState.Text);
            Assert.Equal(((SolidColorBrush)page.FindResource("Success")).Color, ((SolidColorBrush)page.KpiMemState.Foreground).Color);
            Assert.Equal(3, page.ClerksGrid.Items.Count);
            Assert.NotNull(page.ClerksChart.Series);
            Assert.Equal(Visibility.Collapsed, page.TxtNoData.Visibility);
            Assert.Equal("10s", page.CmbInterval.SelectedItem);
            Assert.Equal("--", page.TxtCountdown.Text);

            // The bar's Resume trends it (the countdown is the view model's, tested there).
            page.SetPaused(false);
            Assert.False(page.IsPaused);
            Assert.EndsWith("every 10 s", page.TrendSection.FoldedSummary);
            page.SetPaused(true);

            // The verdict's Show the waiting grants: the Grants tab, the longest waiter selected.
            TemplateRolloutTests.Press(page.Shell.VerdictView, 0);
            Assert.Same(page.TabGrants, page.Tabs.SelectedItem);
            Assert.Equal(77, ((MemoryGrant)page.GrantsGrid.SelectedItem).SessionId);
        });
    });

    // ── Index Health ─────────────────────────────────────────────────────

    private static FakeRepository IndexRepo() => new()
    {
        GetIndexHealthAsync = () => Task.FromResult<(IEnumerable<MissingIndex>, IEnumerable<UnusedIndex>)>((
            [Missing("OrderLines", 54_000, "WARNING", 3_100), Missing("Orders", 310_000, "CRITICAL", 18_400), Missing("Notes", 900, "INFO", 12)], [])),
        GetIndexUsagePatternsAsync = () => Task.FromResult<IEnumerable<IndexUsagePattern>>([]),
        SupportsOnlineIndexRebuildAsync = () => Task.FromResult(true),
        GetIndexFragmentationAsync = (_, _) => Task.FromResult<IEnumerable<IndexFragmentation>>(
            [new("dbo", "Orders", "IX_Orders_Date", "NONCLUSTERED INDEX", 41.5, 120_000, 71.2, 48_000_000, "SalesDb", "Rebuild", DateTime.Now)]),
    };

    [Theory, MemberData(nameof(Themes))]
    public void IndexHealth_Renders_AndEachScriptSaysWhatItCovers(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        var dialogs = new FakeDialogService();
        var page = Wave3Pages.IndexHealth(IndexRepo(), Settings(), dialogs);
        Rendered(theme, page, page.ViewModel.OnNavigatedToAsync, "index-health-vm", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.Tabs);
            Assert.Equal(3, page.MissingGrid.Items.Count);
            Assert.Equal("Generate CREATE Script (all)", page.BtnCreateScript.Content);
            Assert.True(page.BtnCreateScript.IsEnabled);
            Assert.False(page.BtnDropScript.IsEnabled);   // no unused indexes

            page.MissingGrid.SelectedItems.Add(page.MissingGrid.Items[0]);
            page.MissingGrid.SelectedItems.Add(page.MissingGrid.Items[2]);
            Assert.Equal("Generate CREATE Script (2 selected)", page.BtnCreateScript.Content);
            TemplateRolloutTests.Click(page.BtnCreateScript);
            Assert.StartsWith("Review before running. 2 index(es) will be created.", Assert.Single(dialogs.Scripts).Notice);

            page.MissingGrid.SelectedItems.Clear();
            page.MissingFilter.Text = "Notes";
            Assert.Equal("Generate CREATE Script (1 shown)", page.BtnCreateScript.Content);
            page.MissingFilter.Clear();

            // The fragmentation's own Refresh, and a criterion that can't be used, in red.
            page.Tabs.SelectedItem = page.TabFrag;
            page.UpdateLayout();
            Assert.True(page.ChkOnlineRebuild.IsEnabled);
            Assert.True(page.ChkOnlineRebuild.IsChecked);
            Assert.Equal("Generate Maintenance Script (all)", page.BtnMaintenanceScript.Content);
            page.TxtMinFragPct.Text = "0";
            TemplateRolloutTests.Click(page.BtnRefreshFrag);
            Assert.Equal("Min Frag % must be a number greater than 0 and at most 100.", page.TxtFragStatus.Text);
            Assert.Equal(((SolidColorBrush)page.FindResource("Danger")).Color, ((SolidColorBrush)page.TxtFragStatus.Foreground).Color);

            // The verdict's Show it: past the search, in the drawer.
            page.MissingFilter.Text = "OrderLines";
            TemplateRolloutTests.Press(page.Shell.VerdictView, 0);
            Assert.Same(page.TabMissing, page.Tabs.SelectedItem);
            Assert.Equal("", page.MissingFilter.Text);
            Assert.Equal("dbo.Orders", page.Details.Title);
        });
    });

    // ── File I/O ─────────────────────────────────────────────────────────

    private static FakeRepository FileIoRepo()
    {
        var readings = new Queue<FileIoReading>(
        [
            Reading(0, Io(5, "SalesDb", 1, "ROWS", 1_000, 5_000, 100, 200, @"D:\"), Io(5, "SalesDb", 2, "LOG", 0, 0, 1_000, 2_000, @"L:\")),
            Reading(10, Io(5, "SalesDb", 1, "ROWS", 1_400, 26_200, 120, 260, @"D:\"), Io(5, "SalesDb", 2, "LOG", 0, 0, 1_500, 4_000, @"L:\")),
        ]);
        return new FakeRepository { ReadFileIoAsync = () => Task.FromResult(readings.Count > 1 ? readings.Dequeue() : readings.Peek()) };
    }

    [Theory, MemberData(nameof(Themes))]
    public void FileIo_Renders_TheFilesAndVolumes(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        var page = Wave3Pages.FileIo(FileIoRepo(), Settings());
        Rendered(theme, page, async () => { await page.ViewModel.OnNavigatedToAsync(); await page.ViewModel.RefreshAsync(); }, "file-io-vm", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.FilesGrid, minDetail: 150);
            Assert.Equal("2 files", page.TxtCount.Text);
            Assert.Equal("2 volumes", page.TxtVolumes.Text);
            Assert.Equal(("20 ms", "10 ms"), (page.TxtDataMs.Text, page.TxtLogMs.Text));
            Assert.Equal("(red at 5×)", page.TxtCriticalHint.Text);
            Assert.Equal(Visibility.Collapsed, page.TxtError.Visibility);
            Assert.Equal("10s", page.CmbInterval.SelectedItem);

            page.TxtDataMs.Text = "x";
            TemplateRolloutTests.Click(page.BtnApply);
            Assert.Equal(Visibility.Visible, page.TxtError.Visibility);

            // The bar's Pause: Measuring says so.
            page.SetPaused(true);
            Assert.True(page.IsPaused);
            Assert.Contains("(paused)", page.MeasureSection.FoldedSummary);
            page.ViewModel.OnNavigatedFrom();
        });
    });

    [Fact]
    public void FileIo_ReadBeforeItIsLaidOut_KeepsItsColumnsWide() => UiThread.Run(() =>
    {
        // As in the app, the readings can land before the page has been laid out the first time.
        var page = Wave3Pages.FileIo(FileIoRepo(), Settings());
        UiThread.Wait(page.ViewModel.OnNavigatedToAsync());
        UiThread.Wait(page.ViewModel.RefreshAsync());
        page.ViewModel.OnNavigatedFrom();

        TemplateRolloutTests.Hosted(page, () => Task.CompletedTask, null, () =>
            Assert.All(page.FilesGrid.Columns.Where(c => c.Width.IsAbsolute), c => Assert.True(c.ActualWidth > 40, $"{c.Header}: {c.ActualWidth}")));
    });

    // ── Query Regressions ────────────────────────────────────────────────

    [Theory, MemberData(nameof(Themes))]
    public void QueryRegressions_Renders_AndARowsPlanButtonOpensItsPlan(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        var dialogs = new FakeDialogService();
        var page = Wave3Pages.QueryRegressions(TemplateRolloutTests.RegressionsRepo(), null, Settings(), dialogs: dialogs);
        Rendered(theme, page, page.ViewModel.OnNavigatedToAsync, "query-regressions-vm", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.RegressionsGrid, minDetail: 150);
            Assert.Equal("2 regressed queries", page.TxtCount.Text);
            Assert.Equal(("50", "5"), (page.TxtThreshold.Text, page.TxtMinExecutions.Text));
            Assert.Equal("Last 24 hours", page.CmbRecent.SelectedItem);
            Assert.Equal(Visibility.Collapsed, page.TxtError.Visibility);
            Assert.Equal("Last 24 hours", page.HistoryWindow);
            Assert.True(page.UsesDatabase);

            page.RegressionsGrid.SelectedIndex = 0;
            Assert.Equal("Query 4183", page.Details.Title);

            var now = Descendants<Button>(page.RegressionsGrid).First(b => (string)b.Content == "Now");
            TemplateRolloutTests.Click(now);
            Assert.Equal(["Plan: Execution plan used most now (Query Store plan 902) · query 4183"], dialogs.Shown);

            page.TxtThreshold.Text = "nope";
            TemplateRolloutTests.Click(page.BtnCheck);
            UiThread.Wait(Task.Delay(50));
            Assert.Equal(Visibility.Visible, page.TxtError.Visibility);
        });
    });

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
