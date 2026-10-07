using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Backups;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Regressions;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The page template (#146): the verdict first, summary sections that fold and stay folded, and the
/// three pilot pages on it, rendered whole (to %TEMP%\SqlVitalsTests\page-template).
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class PageTemplateTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private static readonly string RenderDir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", "page-template");

    private ConnectionSettingsService Settings() => new(_dir);

    // ── Fold memory ──────────────────────────────────────────────────────

    [Fact]
    public void Folds_SurviveSaveAndLoad_PerPage()
    {
        Settings().SetFolded("Backups", "summary", folded: true);
        Settings().SetFolded("Backups", PageShell.VerdictKey, folded: true);
        Settings().SetFolded("QueryStore", "health", folded: true);
        Settings().SetFolded("Backups", PageShell.VerdictKey, folded: false);

        Assert.Equal(["summary"], Settings().Folded("Backups"));
        Assert.Equal(["health"], Settings().Folded("QueryStore"));
        Assert.Empty(Settings().Folded("Processes"));
    }

    [Fact]
    public void Folds_AreALayoutPreference_NotInTheActivityLog()
    {
        var settings = Settings();
        var logged   = new List<ActivityEntry>();
        settings.Changed += logged.AddRange;

        settings.SetFolded("Backups", "summary", folded: true);

        Assert.Empty(logged);
    }

    // ── The shell ────────────────────────────────────────────────────────

    private sealed class MemoryFolds : ISectionFolds
    {
        public readonly HashSet<string> Keys = [];
        public IReadOnlySet<string> Folded(string page) =>
            Keys.Where(k => k.StartsWith(page + "/", StringComparison.Ordinal)).Select(k => k[(page.Length + 1)..]).ToHashSet();
        public void SetFolded(string page, string section, bool folded)
        {
            if (folded) Keys.Add($"{page}/{section}"); else Keys.Remove($"{page}/{section}");
        }
    }

    private static (PageShell Shell, FoldSection Section) Shell(MemoryFolds folds)
    {
        var section = new FoldSection { Key = "charts", Header = "Charts", FoldedSummary = "CPU 40 %", Body = new Border { Height = 120 } };
        var shell   = new PageShell { Title = "Test", Detail = new Border() };
        shell.Sections.Add(section);
        shell.PageKey = "Test";
        shell.Folds   = folds;
        return (shell, section);
    }

    [Fact]
    public void ASection_FoldsToOneLine_AndThePageRemembersIt() => UiThread.Run(() =>
    {
        var folds = new MemoryFolds();
        var (shell, section) = Shell(folds);
        UiThread.Layout(shell, 800, 600);
        var unfolded = section.ActualHeight;

        section.Toggle.IsChecked = false;   // the user folds it
        UiThread.Layout(shell, 800, 600);

        Assert.True(section.IsFolded);
        Assert.True(section.ActualHeight < unfolded - 100, $"{section.ActualHeight} vs {unfolded}");
        Assert.Equal(["Test/charts"], folds.Keys);

        // The page opened again: folded, as it was left.
        var (_, again) = Shell(folds);
        Assert.True(again.IsFolded);
        Assert.Equal("Charts, folded: CPU 40 %", System.Windows.Automation.AutomationProperties.GetName(again.Toggle));
    });

    [Fact]
    public void TheVerdict_FoldsToItsFirstLine_AndItsActionsRunTheCommand() => UiThread.Run(() =>
    {
        var folds   = new MemoryFolds();
        var (shell, _) = Shell(folds);
        var pressed = new List<VerdictAction>();
        shell.VerdictCommand = new CommunityToolkit.Mvvm.Input.RelayCommand<VerdictAction>(a => pressed.Add(a!));
        shell.Verdict = new PageVerdict(VerdictLevel.Warning, "Something is wrong", "Because.", [new("go", "Go", 7)]);

        var button = Assert.Single(shell.VerdictView.ActionButtons);
        button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal([new VerdictAction("go", "Go", 7)], pressed);
        Assert.Equal(HealthBrushesOf(VerdictLevel.Warning), shell.VerdictView.BorderBrush);

        shell.VerdictView.FoldToggle.IsChecked = false;
        Assert.True(shell.VerdictView.IsFolded);
        Assert.Contains($"Test/{PageShell.VerdictKey}", folds.Keys);
        Assert.Equal(Visibility.Collapsed, button.Parent is FrameworkElement panel ? panel.Visibility : Visibility.Visible);

        shell.Verdict = null;
        Assert.Equal(Visibility.Collapsed, shell.VerdictView.Visibility);
    });

    private static Brush HealthBrushesOf(VerdictLevel level) => VerdictView.Brush(level);

    // ── The pilot pages, whole ───────────────────────────────────────────

    private static void Render(FrameworkElement page, string name)
    {
        if (page.Parent is null)
            UiThread.Layout(page, 1100, 760);
        Directory.CreateDirectory(RenderDir);
        var bitmap = new RenderTargetBitmap((int)page.ActualWidth, (int)page.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(page);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(RenderDir, name));
        encoder.Save(file);
    }

    private static ProcessNode Node(int spid, int blockedBy = 0, long waitMs = 0, string status = "running", int openTran = 0) =>
        new(spid, blockedBy, status, "svc_orders", "APP-WEB03", "OrderService", blockedBy != 0 ? "LCK_M_X" : null, waitMs,
            null, null, "SalesDb", 0, 0, 0, DateTime.MinValue, DateTime.MinValue, openTran, null, null, "");

    [Fact]
    public void Processes_OpensWithTheVerdictOnBlocking() => UiThread.Run(() =>
    {
        var page = new ProcessesPage(EvidencePagesTests.FakeRepository.Create(new()
        {
            [nameof(IWaitStatsRepository.GetProcessesAsync)] = Task.FromResult<IEnumerable<ProcessNode>>(
            [
                Node(87, status: "sleeping", openTran: 1), Node(112, 87, 252_000), Node(115, 87, 231_000), Node(130, 112, 40_000),
                Node(5), Node(6), Node(7),
            ]),
        }), Settings());
        UiThread.Wait(page.RefreshAsync());
        Render(page, "processes.png");

        var verdict = page.Shell.VerdictView;
        Assert.Equal(Visibility.Visible, verdict.Visibility);
        Assert.Equal("Session 87 is blocking 3 sessions, the longest for 4.2 min", verdict.Verdict!.Headline);
        Assert.Equal(VerdictLevel.Critical, verdict.Verdict.Level);
        Assert.True(verdict.ActualHeight > 0 && verdict.ActualHeight < 200, $"verdict {verdict.ActualHeight}");
        Assert.True(page.ProcessMap.ActualHeight > 300, $"map {page.ProcessMap.ActualHeight}");

        // Its actions: Blocking chains only ticks the box.
        verdict.ActionButtons[1].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.True(page.ProcessMap.ChkChainsOnly.IsChecked);
    });

    [Fact]
    public void Processes_ThatCantRead_SaysSoInTheVerdict() => UiThread.Run(() =>
    {
        var page = new ProcessesPage(EvidencePagesTests.FakeRepository.Create(new()
        {
            [nameof(IWaitStatsRepository.GetProcessesAsync)] = Task.FromException<IEnumerable<ProcessNode>>(new InvalidOperationException("Timeout expired.")),
        }), Settings());

        Assert.Throws<InvalidOperationException>(() => UiThread.Wait(page.RefreshAsync()));

        Assert.Equal("Couldn't read the sessions", page.Shell.Verdict!.Headline);
        Assert.Equal("Error: Timeout expired. Refresh to try again.", page.Shell.Verdict.Why);
    });

    private static BackupInfo Ago(DateTime now, TimeSpan age) => new(now - age - TimeSpan.FromMinutes(5), now - age, 1_048_576, false, null);

    [Fact]
    public void Backups_OpensWithTheVerdict_AndTheRpoFoldsToItsCounts() => UiThread.Run(() =>
    {
        var now  = DateTime.Now;
        var rows = new[]
        {
            new DatabaseBackupRow("Sales", "FULL", "ONLINE", false, new DateTime(2024, 1, 1), false, true, true,
                                  Ago(now, TimeSpan.FromDays(1)), null, Ago(now, TimeSpan.FromHours(3))),
            new DatabaseBackupRow("HR", "SIMPLE", "ONLINE", false, new DateTime(2024, 1, 1), false, true, true,
                                  Ago(now, TimeSpan.FromDays(1)), null, null),
        };
        var settings = Settings();
        var page = new BackupsPage(EvidencePagesTests.FakeRepository.Create(new()
        {
            [nameof(IWaitStatsRepository.GetBackupStatusAsync)]  = Task.FromResult(BackupList.From(rows, now, BackupRpo.Default)),
            [nameof(IWaitStatsRepository.GetBackupHistoryAsync)] = Task.FromResult<IReadOnlyList<BackupHistoryEntry>>([]),
        }), settings);
        UiThread.Wait(page.RefreshAsync());
        Render(page, "backups.png");

        Assert.Equal("Sales is past your RPO: log overdue", page.Shell.Verdict!.Headline);

        // "Show only it" picks the filter of the databases that need attention.
        page.Shell.VerdictView.ActionButtons[0].RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
        Assert.Equal(["Sales"], page.DatabasesGrid.Items.Cast<DatabaseBackup>().Select(d => d.Name));

        page.SummarySection.Toggle.IsChecked = false;
        Assert.StartsWith("2 databases  ·  0 without a full backup", page.SummarySection.FoldedSummary);
        Assert.Equal(["summary"], settings.Folded("Backups"));
        Render(page, "backups-folded.png");
    });

    [Fact]
    public void QueryStore_OpensWithTheQueriesThatGotSlower() => UiThread.Run(() =>
    {
        var health = new QueryStoreHealth("SalesDb", "READ_WRITE", "READ_WRITE", 0, 410, 1000, 41, 900, 60, 30, "AUTO", "AUTO", null,
                                          200, 1482, 1630, 0, DateTime.UtcNow.AddDays(-20), DateTime.Now);
        // Query 4183 took 0.5 s a run the week before, and 2.05 s with a new plan now.
        QueryPeriodStats[] stats =
        [
            new("4183", IsRecent: false, PlanId: 311, Executions: 1000, DurationUs: 1000 * 500_000.0, CpuUs: 1000 * 100_000.0),
            new("4183", IsRecent: true,  PlanId: 902, Executions: 200,  DurationUs: 200 * 2_050_000.0, CpuUs: 200 * 100_000.0),
        ];
        var page = new QueryStorePage(EvidencePagesTests.FakeRepository.Create(new()
        {
            [nameof(IWaitStatsRepository.GetQueryStoreStatusAsync)]          = Task.FromResult(new QueryStoreStatus("SalesDb", health, QueryStoreFeatures.None)),
            [nameof(IWaitStatsRepository.GetQueryStoreTopQueriesAsync)]      = Task.FromResult(new QueryStoreRows<QueryStoreQueryRow>(
            [
                new(4183, 0, null, "SELECT o.OrderId, c.Name, SUM(l.Qty) FROM dbo.Orders o JOIN dbo.Customers c ON …", 2, 8_412, 0,
                    18_120_000, 18_120_000, 2_310, 41_000_000, null, 902, DateTime.UtcNow.AddMinutes(-3)),
                new(2210, 0, null, "EXEC dbo.usp_GetCustomerHistory @CustomerId = @p0", 1, 21_930, 0,
                    13_200_000, 13_200_000, 1_420, 22_000_000, null, 455, DateTime.UtcNow.AddMinutes(-1)),
                new(3391, 0, null, "SELECT TOP (50) * FROM dbo.Products WHERE CategoryId = @p0", 1, 55_004, 0,
                    10_500_000, 10_500_000, 1_180, 9_000_000, null, 120, DateTime.UtcNow.AddMinutes(-2)),
            ])),
            [nameof(IWaitStatsRepository.GetQueryStoreRegressionStatsAsync)] = Task.FromResult(new QueryStoreRegressionStats("SalesDb", "READ_WRITE", true, stats)),
        }), Settings());
        // In a window, as in the app, placed off the screen: a DataGrid that isn't laid out while the
        // dispatcher runs (here, while the verdict is read) shrinks its columns to their minimum.
        var window = new Window { Content = page, Width = 1400, Height = 820, Left = -20_000, Top = -20_000,
                                  WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        try
        {
            UiThread.Wait(page.RefreshAsync());
            UiThread.Wait(page.VerdictLoaded);
            UiThread.Wait(Task.Delay(100));   // the window lays out the rows read
            window.UpdateLayout();
            Render(page, "query-store.png");
        }
        finally
        {
            window.Close();
        }

        Assert.Equal("Query Store — SalesDb", page.Shell.Title);
        Assert.Equal("1 query got slower: query 4183 runs 4.1× slower since it got a new plan", page.Shell.Verdict!.Headline);
        Assert.Equal("Last 24 hours compared with the 7 days before. Query Regressions lists each with its plans.", page.Shell.Verdict.Why);
        Assert.Equal("READ_WRITE · 41.0 % of storage · settings OK", page.HealthSection.FoldedSummary);
    });

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
