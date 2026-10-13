using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Engine.AgentJobs;
using SqlVitals.Engine.ConfigChecks;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Security;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The second wave of #180 (#186), Config Checks, Security Audit, Export, Resource Queries,
/// Processes (with its Waiting view) and Agent Jobs, bound to their view models and rendered whole
/// in both themes (to %TEMP%\SqlVitalsTests\page-template-rollout\&lt;page&gt;-&lt;theme&gt;.png). Each
/// fails on any binding error. What their view models decide is tested without a page, in their
/// own tests; the pages' earlier tests (TemplateEverywhereTests, TemplateRolloutTests,
/// EvidencePagesTests, DetailsDrawerTests) still drive them whole.
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class Wave2PagesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));

    private ConnectionSettingsService Settings() => new(_dir);

    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    private static readonly DateTime Now = DateTime.Now;

    // Renders the page, its view model loading it, and fails on any binding error. The test puts
    // the theme on before it builds the page: its StaticResource brushes are looked up then.
    private static void Rendered(string theme, Page page, Func<Task> load, string name, Action whileShown)
    {
        using var errors = BindingErrors.Watch();
        TemplateRolloutTests.Hosted(page, load, $"{name}-{theme}.png", whileShown);
        errors.AssertNone();
    }

    // ── Config Checks and Security Audit ─────────────────────────────────

    private static FakeRepository ChecksRepo() => new()
    {
        GetConfigurationChecksAsync = () => Task.FromResult(new ConfigCheckList(ConfigCheckList.Sort(
        [
            new(ConfigCheckKind.MaxDop, ConfigCheck.ServerScope, ConfigCheckStatus.Warn, "0 (all 16 cores)", "8",
                "With MAXDOP 0 one query can take every core.", "EXEC sp_configure 'max degree of parallelism', 8;"),
            new(ConfigCheckKind.CostThreshold, ConfigCheck.ServerScope, ConfigCheckStatus.Pass, "50", "50 or more", "Fine."),
            new(ConfigCheckKind.MaxServerMemory, ConfigCheck.ServerScope, ConfigCheckStatus.NotChecked, "—", "28,672 MB", "Needs VIEW SERVER STATE."),
        ]), "1 database couldn't be read.")),
    };

    [Theory, MemberData(nameof(Themes))]
    public void ConfigChecks_Renders_WithItsChecksAndTheirScript(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        var dialogs = new FakeDialogService();
        var page    = Wave2Pages.ConfigChecks(ChecksRepo(), "PROD-SQL01", Settings(), dialogs);
        Rendered(theme, page, page.ViewModel.OnNavigatedToAsync, "config-checks", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.ChecksGrid);
            Assert.Equal(3, page.ChecksGrid.Items.Count);
            Assert.Equal("3 results", page.TxtCount.Text);
            Assert.Equal((Visibility.Visible, "1 database couldn't be read."), (page.TxtProblem.Visibility, page.TxtProblem.Text));
            Assert.StartsWith("Checked at ", page.TxtStatus.Text);
            Assert.Equal(Visibility.Collapsed, page.TxtNoData.Visibility);
            Assert.Equal("MAXDOP · Server", page.Details.Title);

            // The filter is the view model's; a filter that leaves none says why.
            page.CmbFilter.SelectedIndex = 4;
            Assert.Single(page.ChecksGrid.Items);
            page.CmbFilter.SelectedIndex = 3;   // database settings: none
            page.UpdateLayout();
            Assert.Equal(Visibility.Visible, page.TxtNoData.Visibility);
            page.CmbFilter.SelectedIndex = 0;

            Assert.True(page.BtnScript.IsEnabled);
            TemplateRolloutTests.Click(page.BtnScript);
            Assert.Equal("Configuration Fixes", Assert.Single(dialogs.Scripts).Heading);
        });
    });

    [Fact]
    public void ConfigChecks_ReadBeforeItIsLaidOut_KeepsItsColumnsWide() => UiThread.Run(() =>
    {
        // As in the app, the read can land before the page has been laid out the first time.
        var page = Wave2Pages.ConfigChecks(ChecksRepo(), "PROD-SQL01", Settings());
        UiThread.Wait(page.ViewModel.OnNavigatedToAsync());

        TemplateRolloutTests.Hosted(page, () => Task.CompletedTask, null, () =>
            Assert.All(page.ChecksGrid.Columns, c => Assert.True(c.ActualWidth > 60, $"{c.Header}: {c.ActualWidth}")));
    });

    [Fact]
    public void SecurityAudit_ReadBeforeItIsLaidOut_KeepsItsColumnsWide() => UiThread.Run(() =>
    {
        var page = Wave2Pages.SecurityAudit(new FakeRepository
        {
            GetSecurityAuditAsync = () => Task.FromResult(new SecurityFindingList(
                [new(SecurityCheckKind.SaLogin, SecurityFinding.ServerScope, "sa", SecurityFindingStatus.Risk, "SQL login · enabled", "Risky.", "ALTER LOGIN [sa] DISABLE;")], null)),
        }, "PROD-SQL01", Settings());
        UiThread.Wait(page.ViewModel.OnNavigatedToAsync());

        TemplateRolloutTests.Hosted(page, () => Task.CompletedTask, null, () =>
            Assert.All(page.FindingsGrid.Columns, c => Assert.True(c.ActualWidth > 60, $"{c.Header}: {c.ActualWidth}")));
    });

    [Theory, MemberData(nameof(Themes))]
    public void SecurityAudit_Renders_WithItsFindings(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        var page = Wave2Pages.SecurityAudit(new FakeRepository
        {
            GetSecurityAuditAsync = () => Task.FromResult(new SecurityFindingList(SecurityFindingList.Sort(
            [
                new(SecurityCheckKind.SaLogin, SecurityFinding.ServerScope, "sa", SecurityFindingStatus.Risk, "SQL login · enabled",
                    "sa is the first login an attacker tries.", "ALTER LOGIN [sa] DISABLE;"),
                new(SecurityCheckKind.Trustworthy, "SalesDb", "SalesDb", SecurityFindingStatus.Pass, "OFF", "Fine."),
            ]), null)),
        }, "PROD-SQL01", Settings());
        Rendered(theme, page, page.ViewModel.OnNavigatedToAsync, "security-audit", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.FindingsGrid);
            Assert.Equal("2 findings", page.TxtCount.Text);
            Assert.Equal(Visibility.Collapsed, page.TxtProblem.Visibility);
            Assert.Equal("ALTER LOGIN [sa] DISABLE;", page.DetailsView!.Text);
            Assert.True(page.BtnScript.IsEnabled);
        });
    });

    // ── Export ───────────────────────────────────────────────────────────

    [Theory, MemberData(nameof(Themes))]
    public void Export_Renders_WithEverySection(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        var page = Wave2Pages.Export(new FakeRepository(), Settings());
        page.TxtFilePath.Text = @"C:\Reports";
        Rendered(theme, page, page.ViewModel.OnNavigatedToAsync, "export", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.GroupList, minDetail: 150);
            Assert.Equal(ExportService.AllGroups.Length, page.GroupList.Items.Count);
            Assert.Equal(@"C:\Reports", page.ViewModel.FilePath);
            Assert.Equal(@"C:\Reports\SqlVitals_Report.txt", page.TxtFullPath.Text);
            Assert.Equal($"{ExportService.AllGroups.Length} of {ExportService.AllGroups.Length} selected", page.TxtCheckedCount.Text);
            Assert.Equal(Visibility.Collapsed, page.ExportProgress.Visibility);

            page.ViewModel.Sections[0].IsChecked = false;
            Assert.StartsWith($"{ExportService.AllGroups.Length - 1} of", page.TxtCheckedCount.Text);
            TemplateRolloutTests.Click(page.BtnSelectNone);
            Assert.False(page.BtnExport.IsEnabled);
        });
    });

    // ── Resource Queries ─────────────────────────────────────────────────

    private static ResourceIntensiveQuery Query(string? obj, long cpu, long reads, string text) =>
        new(text, "SalesDb", obj, 100, reads, reads / 100, cpu, cpu / 100, cpu * 2, cpu / 50, 0, 0,
            Now.AddMinutes(-5), Now.AddHours(-5), Now, "<plan/>", reads / 100, obj is null ? "Ad-Hoc" : "Stored Proc");

    [Theory, MemberData(nameof(Themes))]
    public void ResourceQueries_Renders_WithEveryTab_AndViewPlan(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        ResourceIntensiveQuery[] queries =
        [
            Query("dbo.usp_GetOrderHistory", 812_000_000, 98_120_000, "SELECT o.OrderId FROM dbo.Orders o"),
            Query(null, 241_000_000, 410_220_000, "SELECT * FROM dbo.Stock WHERE Sku LIKE @p0"),
        ];
        var dialogs = new FakeDialogService();
        var page = Wave2Pages.ResourceQueries(new FakeRepository
        {
            GetResourceIntensiveQueriesAsync = _ => Task.FromResult<(IEnumerable<ResourceIntensiveQuery>, IEnumerable<ResourceIntensiveQuery>, IEnumerable<ResourceIntensiveQuery>)>(
                (queries.OrderByDescending(q => q.TotalLogicalReads), queries.OrderByDescending(q => q.TotalCPUTime), queries)),
            GetObjectQueryPlanAsync = _ => Task.FromResult<string?>("<object plan/>"),
        }, folds: Settings(), dialogs: dialogs);
        Rendered(theme, page, page.ViewModel.OnNavigatedToAsync, "resource-queries", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.Tabs);
            Assert.Equal(20, page.CmbTopN.SelectedItem);
            foreach (var (tab, grid) in new (int, DataGrid)[] { (0, page.ReadsGrid), (1, page.CpuGrid), (2, page.HighestReadsGrid) })
            {
                page.Tabs.SelectedIndex = tab;
                Assert.Equal(tab, page.ViewModel.SelectedTab);
                UiThread.Wait(Task.Delay(50));
                page.UpdateLayout();
                Assert.Equal(2, grid.Items.Count);
            }

            // A row's View Plan: the command, for its row.
            var button = UiThread.Find<Button>(page.HighestReadsGrid).First(b => (string)b.Content == "View Plan");
            TemplateRolloutTests.Click(button);
            UiThread.Wait(Task.Delay(50));
            Assert.Equal(["<object plan/>"], dialogs.Plans);
        });
    });

    // ── Processes ────────────────────────────────────────────────────────

    private static ProcessNode Node(int spid, int blockedBy = 0, long waitMs = 0) =>
        new(spid, blockedBy, blockedBy != 0 ? "suspended" : "sleeping", "svc_orders", "APP-WEB03", "OrderService", blockedBy != 0 ? "LCK_M_X" : null, waitMs,
            null, null, "SalesDb", 0, 0, 0, DateTime.MinValue, DateTime.MinValue, 0, null, null, "");

    private static ActiveWait Wait(int spid, int blockedBy) =>
        new(spid, blockedBy, "LCK_M_X", 252_000, 252, null, "KEY: 7:72057594043039744", "suspended", "UPDATE", 1_240, 252_040, 18_400, 12, 3, 0, 1,
            "svc_orders", "APP-WEB03", "OrderService", "SalesDb", "Locks", 1, "UPDATE dbo.Orders SET Status = @p0", Now, "Critical");

    [Theory, MemberData(nameof(Themes))]
    public void Processes_Renders_OnEachView(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        var page = Wave2Pages.Processes(new FakeRepository
        {
            GetProcessesAsync   = () => Task.FromResult<IEnumerable<ProcessNode>>([Node(87), Node(112, 87, 252_000), Node(5)]),
            GetActiveWaitsAsync = () => Task.FromResult<IEnumerable<ActiveWait>>([Wait(112, 87)]),
        }, Settings());
        Rendered(theme, page, page.ViewModel.OnNavigatedToAsync, "processes-map", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.ProcessMap);
            Assert.True(page.BtnMap.IsChecked);
            Assert.Equal(3, page.ProcessMap.SessionCount);
            Assert.Equal("--", page.TxtCountdown.Text);
            Assert.Equal("Start", page.BtnAutoRefresh.Content);
            Assert.Equal(ConnectionStore.DefaultProcessesRefreshSeconds, ((SqlVitals.Desktop.ViewModels.Pages.ProcessesViewModel.Interval)page.CmbInterval.SelectedItem).Seconds);

            page.BtnGrid.IsChecked = true;
            Assert.True(page.ProcessMap.ShowGrid);

            page.BtnWaiting.IsChecked = true;
            UiThread.Wait(Task.Delay(50));
            page.UpdateLayout();
            Assert.Equal((Visibility.Collapsed, Visibility.Visible), (page.ProcessMap.Visibility, page.WaitingPanel.Visibility));
            Assert.Single(page.WaitingGrid.Items);
            TemplateRolloutTests.Render(page, $"processes-waiting-bound-{theme}.png");

            page.BtnAutoRefresh.IsChecked = true;
            Assert.Equal("Stop", page.BtnAutoRefresh.Content);
            Assert.False(page.ViewModel.IsPaused);   // its countdown: PageRefreshTests, on a fake clock
        });
        page.ViewModel.OnNavigatedFrom();
    });

    // ── Agent Jobs ───────────────────────────────────────────────────────

    private static AgentJob Job(string name, JobOutcome last) =>
        new(Guid.NewGuid(), name, true, "Database Maintenance", "sa", $"{name}, nightly.", last, Now.AddHours(-6), TimeSpan.FromMinutes(12), null,
            30, last == JobOutcome.Failed ? 2 : 0, 28, TimeSpan.FromMinutes(12), null, null,
            Now.AddHours(18), Now.AddHours(18).ToString("yyyy-MM-dd HH:mm", CultureInfo.CurrentCulture));

    [Theory, MemberData(nameof(Themes))]
    public void AgentJobs_Renders_WithAJobsRunsAndSteps(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        static JobStepRun Step(int id, JobOutcome outcome) => new(id, $"Step {id}", outcome, Now.AddHours(-6), TimeSpan.FromMinutes(3), $"Step {id} said so.");
        var page = Wave2Pages.AgentJobs(new FakeRepository
        {
            GetAgentJobsAsync    = () => Task.FromResult(new AgentJobList(AgentJobList.Sort([Job("Nightly backup", JobOutcome.Failed), Job("Purge history", JobOutcome.Succeeded)]),
                                                                          AgentServiceState.Running, Now, null)),
            GetAgentJobRunsAsync = (_, _) => Task.FromResult<IReadOnlyList<JobRun>>(
                [new(Now.AddHours(-6), JobOutcome.Failed, TimeSpan.FromMinutes(6), "The job failed.", [Step(1, JobOutcome.Succeeded), Step(2, JobOutcome.Failed)])]),
        }, Settings());
        Rendered(theme, page, async () => { await page.ViewModel.OnNavigatedToAsync(); await page.ViewModel.RunsRead; }, "agent-jobs", () =>
        {
            TemplateRolloutTests.AssertLaidOut(page.Shell, page.JobsGrid, minDetail: 100);
            Assert.Equal("2 jobs", page.TxtCount.Text);
            Assert.Equal("Nightly backup", ((AgentJob)page.JobsGrid.SelectedItem).Name);
            Assert.True(page.DetailPanel.IsEnabled);
            Assert.StartsWith("Nightly backup · ", page.TxtDetailHeader.Text);
            Assert.Equal("1 run msdb keeps, newest first", page.TxtRunsLabel.Text);
            Assert.Equal(2, ((JobStepRun)page.StepsGrid.SelectedItem).StepId);
            Assert.Contains("Step 2 said so.", page.DetailsView!.Text);
            Assert.StartsWith("Agent running · read at ", page.SourceSection.FoldedSummary);

            page.ChkAutoRefresh.IsChecked = true;
            Assert.Equal(PageKind.Live, page.Kind);
        });
        page.ViewModel.OnNavigatedFrom();
    });

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
