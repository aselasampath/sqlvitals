using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Backups;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The MVVM pilot's pages (#184), TempDB, Stale Stats and Backups, bound to their view models and
/// rendered whole in both themes (to %TEMP%\SqlVitalsTests\page-template-rollout\&lt;page&gt;-&lt;theme&gt;.png).
/// Each fails on any binding error, which WPF would otherwise only write to a debugger's output.
/// What their view models decide is tested without a page, in their own tests.
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class PilotPagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private ConnectionSettingsService Settings() => new(_dir);

    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    private static readonly DateTime Now = DateTime.Now;

    private static Color Colour(FrameworkElement element, string key) => ((SolidColorBrush)element.FindResource(key)).Color;

    // ── The watcher itself ───────────────────────────────────────────────

    [Fact]
    public void BindingErrors_CatchesABrokenBinding() => UiThread.Run(() =>
    {
        using var errors = BindingErrors.Watch();
        var text = new TextBlock { DataContext = new { Name = "x" } };
        text.SetBinding(TextBlock.TextProperty, new Binding("NoSuchProperty"));
        UiThread.Layout(text, 100, 20);

        Assert.Contains(errors.Errors, e => e.Contains("NoSuchProperty", StringComparison.Ordinal));
    });

    [Fact]
    public void BindingErrors_IgnoresOnlyWpfsOwnItemAlignmentError() => UiThread.Run(() =>
    {
        using var errors = BindingErrors.Watch();
        const string own = "System.Windows.Data Error: 4 : Cannot find source for binding with reference 'RelativeSource FindAncestor, " +
                           "AncestorType='System.Windows.Controls.ItemsControl', AncestorLevel='1''. BindingExpression:Path=HorizontalContentAlignment; " +
                           "DataItem=null; target element is 'ComboBoxItem' (Name=''); target property is 'HorizontalContentAlignment' (type 'HorizontalAlignment')";
        const string ours = "System.Windows.Data Error: 4 : Cannot find source for binding with reference 'ElementName=StatsGrid'. " +
                            "BindingExpression:Path=Items; DataItem=null; target element is 'Button' (Name='BtnUpdateStatsScript')";

        errors.WriteLine(own);
        errors.WriteLine(ours);

        Assert.Equal([ours], errors.Errors);
    });

    // ── TempDB ───────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(Themes))]
    public void TempDb_Renders_WithItsCardsChartAndSessions(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        using var errors = BindingErrors.Watch();
        var model = new TempDbViewModel(new FakeRepository
        {
            GetTempDbPressureAsync = () => Task.FromResult<(IEnumerable<TempDbFile>, IEnumerable<TempDbSession>, IEnumerable<TempDbCounter>)>((
                [new("tempdev", "ROWS", @"T:\tempdb.mdf", 8_192, 2_100, 6_092, 25.6, "64 MB", Now),
                 new("templog", "LOG", @"T:\templog.ldf", 2_048, 300, 1_748, 14.6, "64 MB", Now)],
                [new(88, 20_480, 0, 870_400, 0, 890_880, "etl", "APP-ETL01", "Loader", "PAGELATCH_UP", "running", 4_000, "SalesDb", null, Now, "Critical"),
                 new(91, 4_096, 0, 0, 0, 4_096, "app", "APP-WEB03", "OrderService", null, "sleeping", 10, "SalesDb", null, Now, "Info")],
                [new("Version Store Size (KB)", 12_288, Now), new("Free Space in tempdb (KB)", 900_000, Now), new("Active Temp Tables", 42, Now)])),
        });
        var page = new TempDbPage(model, Settings());

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, $"tempdb-{theme}.png", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.SessionsGrid, minDetail: 150);
            Assert.Equal(2, page.SessionsGrid.Items.Count);
            Assert.Equal(3, page.CounterGrid.Items.Count);
            // The version store's card in the danger colour, free space low in the warning one.
            var values = Values(page.CounterGrid);
            Assert.Equal(["12,288", "900,000", "42"], values.Select(v => v.Text));
            Assert.Equal([Colour(page, "Danger"), Colour(page, "Warning"), Colour(page, "Accent")],
                         values.Select(v => ((SolidColorBrush)v.Foreground).Color));
            Assert.Equal(2, page.FileChart.Series.Count());
            Assert.Equal(model.FilesSummary, page.FilesSection.FoldedSummary);

            TemplateRolloutTests.Press(page.Shell.VerdictView, 0);
            Assert.Equal(88, ((TempDbSession)page.SessionsGrid.SelectedItem).SessionId);
        });
        errors.AssertNone();
        model.OnNavigatedFrom();
    });

    // The value of each counter's card: the first text of each.
    private static List<TextBlock> Values(ItemsControl cards) =>
        Enumerable.Range(0, cards.Items.Count)
            .Select(i => Descendants<TextBlock>(cards.ItemContainerGenerator.ContainerFromIndex(i)).First())
            .ToList();

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found)
                yield return found;
            foreach (var deeper in Descendants<T>(child))
                yield return deeper;
        }
    }

    // ── Stale Stats ──────────────────────────────────────────────────────

    private static StaleStatistic Stat(string table, string name, long rows, long changes, int days) =>
        new("dbo", table, name, Now.AddDays(-days), days, rows, rows / 4, 25, changes, false, false, false, "SalesDb", Now);

    [Theory, MemberData(nameof(Themes))]
    public void StaleStats_Renders_AndScriptsTheRowsSelected(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        using var errors = BindingErrors.Watch();
        var dialogs = new FakeDialogService();
        var model = new StaleStatisticsViewModel(new FakeRepository
        {
            GetStaleStatisticsAsync = () => Task.FromResult<IEnumerable<StaleStatistic>>(
                [Stat("Orders", "IX_Orders_Date", 48_000_000, 2_100_000, 9), Stat("OrderLines", "_WA_Sys_00000003_2A4B4B5E", 900_000, 410_000, 31),
                 Stat("Customers", "PK_Customers", 1_200_000, 12_000, 4)]),
        }, dialogs);
        var page = new StaleStatisticsPage(model, Settings());
        Assert.False(page.BtnUpdateStatsScript.IsEnabled);

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, $"stale-stats-{theme}.png", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.StatsGrid);
            Assert.True(page.BtnUpdateStatsScript.IsEnabled);
            Assert.Equal("Generate UPDATE STATISTICS Script (all)", page.BtnUpdateStatsScript.Content);

            // Two rows picked in the grid: the view tells the view model, and the script is theirs.
            page.StatsGrid.SelectedItems.Add(page.StatsGrid.Items[0]);
            page.StatsGrid.SelectedItems.Add(page.StatsGrid.Items[2]);
            Assert.Equal("Generate UPDATE STATISTICS Script (2 selected)", page.BtnUpdateStatsScript.Content);
            page.RbFullScan.IsChecked = true;
            Assert.True(model.FullScan);
            page.BtnUpdateStatsScript.Command.Execute(page.BtnUpdateStatsScript.CommandParameter);

            var script = Assert.Single(dialogs.Scripts);
            Assert.Contains("2 statistic(s) on 2 table(s) will be updated WITH FULLSCAN", script.Notice);

            // A refresh keeps both selected, in the grid too.
            UiThread.Wait(model.RefreshAsync());
            Assert.Equal(["Orders", "Customers"], page.StatsGrid.SelectedItems.Cast<StaleStatistic>().Select(s => s.TableName).Order().Reverse());
            Assert.Equal("Generate UPDATE STATISTICS Script (2 selected)", page.BtnUpdateStatsScript.Content);
        });
        errors.AssertNone();
        model.OnNavigatedFrom();
    });

    // A page bound to its view model, opened again, comes back as it was left (#139): ViewState
    // waits for the bound rows, which are there but empty until the page reads.
    [Fact]
    public void StaleStats_OpenedAgain_GetsItsSortAndSelectionBack() => UiThread.Run(() =>
    {
        var repo = new FakeRepository
        {
            GetStaleStatisticsAsync = () => Task.FromResult<IEnumerable<StaleStatistic>>(
                [Stat("Orders", "IX_Orders_Date", 48_000_000, 2_100_000, 9), Stat("OrderLines", "_WA_Sys_00000003_2A4B4B5E", 900_000, 410_000, 31),
                 Stat("Customers", "PK_Customers", 1_200_000, 12_000, 4)]),
        };
        var firstModel = new StaleStatisticsViewModel(repo, new FakeDialogService());
        var first = new StaleStatisticsPage(firstModel);
        Helpers.ViewState? state = null;
        TemplateRolloutTests.Hosted(first, firstModel.OnNavigatedToAsync, null, () =>
        {
            first.StatsGrid.Items.SortDescriptions.Add(new SortDescription(nameof(StaleStatistic.TableName), ListSortDirection.Ascending));
            first.StatsGrid.SelectedItem = first.StatsGrid.Items.Cast<StaleStatistic>().Single(s => s.TableName == "Orders");
            state = Helpers.ViewState.Capture(first);
        });
        firstModel.OnNavigatedFrom();

        // Read from a server: the rows come after the dispatcher has gone idle once.
        var slow = new FakeRepository
        {
            GetStaleStatisticsAsync = async () => { await Task.Delay(200); return await repo.GetStaleStatisticsAsync!(); },
        };
        var model = new StaleStatisticsViewModel(slow, new FakeDialogService());
        var again = new StaleStatisticsPage(model);
        state!.Restore(again);   // as the window does: built, not yet read

        TemplateRolloutTests.Hosted(again, model.OnNavigatedToAsync, null, () =>
        {
            // It puts them back once the dispatcher is idle, after the rows came.
            UiThread.WaitUntil(() => again.StatsGrid.Items.SortDescriptions.Count > 0 && again.StatsGrid.SelectedItem is not null);
            Assert.Equal(["Customers", "OrderLines", "Orders"], again.StatsGrid.Items.Cast<StaleStatistic>().Select(s => s.TableName));
            Assert.Equal("Orders", ((StaleStatistic)again.StatsGrid.SelectedItem).TableName);
            Assert.Same(again.StatsGrid.SelectedItem, model.SelectedStatistic);
        });
        model.OnNavigatedFrom();
    });

    // ── Backups ──────────────────────────────────────────────────────────

    private static BackupInfo Ago(TimeSpan age) => new(Now - age - TimeSpan.FromMinutes(5), Now - age, 1_048_576, false, @"B:\backups\x.bak");

    private static FakeRepository BackupsRepo(List<BackupRpo>? readWith = null) => new()
    {
        GetBackupStatusAsync = rpo =>
        {
            readWith?.Add(rpo);
            return Task.FromResult(BackupList.From(
            [
                new DatabaseBackupRow("Sales", "FULL", "ONLINE", false, new DateTime(2024, 1, 1), false, true, true,
                                      Ago(TimeSpan.FromDays(1)), null, Ago(TimeSpan.FromHours(3))),
                new DatabaseBackupRow("HR", "SIMPLE", "ONLINE", false, new DateTime(2024, 1, 1), false, true, true,
                                      Ago(TimeSpan.FromDays(1)), null, null),
                new DatabaseBackupRow("Archive", "SIMPLE", "ONLINE", false, new DateTime(2024, 1, 1), false, true, true, null, null, null),
            ], Now, rpo));
        },
        GetBackupHistoryAsync = (_, _) => Task.FromResult<IReadOnlyList<BackupHistoryEntry>>(
            [new("Full", Now.AddDays(-1).AddMinutes(-5), Now.AddDays(-1), 1_048_576, 524_288, false, true, false, "FULL", "sa", @"B:\backups\x.bak", false)]),
    };

    [Theory, MemberData(nameof(Themes))]
    public void Backups_Renders_WithTheSummaryTheDatabasesAndTheSelectedOnesHistory(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        using var errors = BindingErrors.Watch();
        var model = new BackupsViewModel(BackupsRepo(), Settings());
        var page  = new BackupsPage(model, Settings());

        TemplateRolloutTests.Hosted(page, async () => { await model.OnNavigatedToAsync(); await model.HistoryRead; }, $"backups-{theme}.png", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.DatabasesGrid, minDetail: 100);
            Assert.Equal(3, page.DatabasesGrid.Items.Count);
            Assert.Same(model.SelectedDatabase, page.DatabasesGrid.SelectedItem);
            Assert.Single(page.HistoryGrid.Items);
            Assert.True(page.DetailPanel.IsEnabled);
            Assert.Equal("3 databases", page.TxtCount.Text);
            Assert.Equal(Visibility.Collapsed, page.TxtError.Visibility);
            Assert.Equal(Visibility.Collapsed, page.TxtProblem.Visibility);
            Assert.Equal(Visibility.Collapsed, page.TxtNoData.Visibility);
            Assert.Equal(Visibility.Visible, page.SummaryParts.Visibility);
            Assert.Equal(5, page.CmbFilter.Items.Count);

            // "1 without a full backup" in the danger colour.
            var parts = Descendants<TextBlock>(page.SummaryParts).ToList();
            Assert.Equal(Colour(page, "Danger"), ((SolidColorBrush)parts.Single(p => p.Text.EndsWith("without a full backup", StringComparison.Ordinal)).Foreground).Color);

            // An age that can't be used says why, under the boxes.
            page.TxtLogAge.Text = "soon";
            UiThread.Wait(model.ApplyCommand.ExecuteAsync(null));
            page.UpdateLayout();
            Assert.Equal(Visibility.Visible, page.TxtError.Visibility);
            Assert.StartsWith("Enter the log backup age", page.TxtError.Text);
        });
        errors.AssertNone();
        model.OnNavigatedFrom();
    });

    // How a bound grid keeps the user's sort and filter (#184): its rows are refilled, not replaced.
    [Fact]
    public void Backups_ARefresh_KeepsTheSortTheFilterAndTheSelection() => UiThread.Run(() =>
    {
        var model = new BackupsViewModel(BackupsRepo(), Settings());
        var page  = new BackupsPage(model, Settings());

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            page.DatabasesGrid.Items.SortDescriptions.Add(new SortDescription("Name", ListSortDirection.Descending));
            var filter = Descendants<GridFilterBox>(page).Single();
            filter.Text = "Archive";
            page.DatabasesGrid.SelectedItem = page.DatabasesGrid.Items.Cast<DatabaseBackup>().Single();

            UiThread.Wait(model.RefreshAsync());
            UiThread.Wait(model.HistoryRead);

            // Still filtered, and still selected: the new row of the same database.
            Assert.Equal("Archive", filter.Text);
            Assert.Equal("Archive", Assert.Single(page.DatabasesGrid.Items.Cast<DatabaseBackup>()).Name);
            Assert.Equal("Archive", ((DatabaseBackup)page.DatabasesGrid.SelectedItem).Name);
            Assert.Same(model.SelectedDatabase, page.DatabasesGrid.SelectedItem);

            // Still sorted by name, Z to A.
            filter.Text = "";
            Assert.Equal(["Sales", "HR", "Archive"], page.DatabasesGrid.Items.Cast<DatabaseBackup>().Select(d => d.Name));
        });
        model.OnNavigatedFrom();
    });

    // A grid the page read with no rows (a filter that matches none) is still how it was left.
    [Fact]
    public void Backups_LeftWithNoRowsShown_StillGetsItsSortBack() => UiThread.Run(() =>
    {
        var firstModel = new BackupsViewModel(BackupsRepo(), Settings());
        var first = new BackupsPage(firstModel, Settings());
        Helpers.ViewState? state = null;
        TemplateRolloutTests.Hosted(first, firstModel.OnNavigatedToAsync, null, () =>
        {
            first.DatabasesGrid.Items.SortDescriptions.Add(new SortDescription(nameof(DatabaseBackup.Name), ListSortDirection.Ascending));
            firstModel.FilterIndex = 4;   // Not online: none
            Assert.Empty(first.DatabasesGrid.Items);
            state = Helpers.ViewState.Capture(first);
        });
        firstModel.OnNavigatedFrom();

        var model = new BackupsViewModel(BackupsRepo(), Settings());
        var again = new BackupsPage(model, Settings());
        state!.Restore(again);

        TemplateRolloutTests.Hosted(again, model.OnNavigatedToAsync, null, () =>
        {
            UiThread.WaitUntil(() => again.DatabasesGrid.Items.SortDescriptions.Count > 0);
            Assert.Equal(["Archive", "HR", "Sales"], again.DatabasesGrid.Items.Cast<DatabaseBackup>().Select(d => d.Name));
        });
        model.OnNavigatedFrom();
    });

    // Enter in an RPO box applies it, as the button does.
    [Fact]
    public void Backups_EnterInAnRpoBox_ReadsWithIt() => UiThread.Run(() =>
    {
        var readWith = new List<BackupRpo>();
        var model = new BackupsViewModel(BackupsRepo(readWith), Settings());
        var page  = new BackupsPage(model, Settings());

        TemplateRolloutTests.Hosted(page, model.OnNavigatedToAsync, null, () =>
        {
            page.TxtFullAge.Text = "3";
            page.TxtFullAge.Focus();
            var source = PresentationSource.FromVisual(page.TxtFullAge)!;
            page.TxtFullAge.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, Key.Enter) { RoutedEvent = Keyboard.KeyDownEvent });
            UiThread.WaitUntil(() => readWith.Count == 2 && !model.IsBusy);

            Assert.Equal(TimeSpan.FromDays(3), readWith[^1].FullMaxAge);
        });
        model.OnNavigatedFrom();
    });

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
