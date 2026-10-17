using System.IO;
using Microsoft.Extensions.Logging.Abstractions;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Services.Wpf;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Shell;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The main window's logic on its own (#191): no window and no page, the window's side faked
/// (<see cref="FakePageHost"/>). Two saved servers, PROD-A (the active one) and PROD-B; opening a
/// server's page switches to it, Back and Forward go through the visits, a page the server hasn't
/// got opens its Overview, only the page on screen owns the top bar, and the quick switcher opens
/// what it finds.
/// </summary>
public sealed class ShellViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly FakeUiDispatcher _ui = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly FakePageHost _host = new();
    private readonly Repositories _repositories = new();
    private readonly ConnectionSettings _a = Conn("prod-a", "PROD-A");
    private readonly ConnectionSettings _b = Conn("prod-b", "PROD-B");

    private ConnectionSettingsService _settings = null!;
    private PageFactory _pages = null!;
    private ShellViewModel? _shell;

    private static ConnectionSettings Conn(string server, string name) => new()
    {
        Server         = server,
        Name           = name,
        Database       = "Sales",
        Authentication = SqlAuthMode.Windows,
    };

    public void Dispose()
    {
        _shell?.Shutdown(Engine.Monitoring.WatchStop.AppExit);
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    // The shell over the saved connections, the first active, started and showing its first page.
    private ShellViewModel Started(params ConnectionSettings[] connections)
    {
        Directory.CreateDirectory(_dir);
        _settings = new ConnectionSettingsService(_dir);
        foreach (var conn in connections)
            _settings.Upsert(conn);
        if (connections.Length > 0)
            _settings.SetActive(connections[0].Id);

        var monitoring = new MonitoringManager(new HistoryWriter(Path.Combine(_dir, "history.db"), (_, _) => { }),
                                               (_, _) => new FakeRepository { Unreachable = true });
        _pages = new PageFactory(this);
        _shell = new ShellViewModel(_pages, _dialogs, _settings, monitoring,
                                    new ChatNotificationService(new HttpWebhookSender(), "DBA-PC01", _ => { }),
                                    _repositories, new CollectorLink(new FakeCollectorControl(_dir), _settings, _ui, TimeProvider.System),
                                    new DirectWatchStarter(monitoring), _ui);
        _shell.Attach(_host);
        _shell.Start(windowWidth: 1320);
        UiThread.Wait(_shell.OpenFirstPageAsync());
        return _shell;
    }

    private string Breadcrumb => _shell!.TopBar.BreadcrumbText;

    private StubPage Page => (StubPage)_shell!.CurrentPage!;

    // ── Servers ──────────────────────────────────────────────────────────

    [Fact]
    public void ItStarts_OnHome_WithMoreThanOneServer_TheServersByName() => OnDispatcher.Run(() =>
    {
        var shell = Started(_b, _a);

        Assert.Equal(["PROD-A", "PROD-B"], shell.Servers.Select(s => s.DisplayName));
        Assert.Equal("Home", shell.Current!.Tag);
        Assert.Equal("All servers", Breadcrumb);
        Assert.Null(shell.Sidebar.Server);
        Assert.Equal("Home", shell.Sidebar.Current);
        Assert.Equal(1, Page.Loads);
        Assert.Equal(["Restore Home", "Scroll Home"], _host.Calls);
        Assert.Equal(_b.Id, shell.ActiveConnectionId);
        Assert.True(shell.TopBar.CanRefresh);
        Assert.Equal(string.Empty, shell.TopBar.Message);
    });

    [Fact]
    public void OpeningAnotherServersPage_SwitchesToIt_AndItsWorkspaceShows() => OnDispatcher.Run(() =>
    {
        var shell = Started(_a, _b);

        UiThread.Wait(shell.OpenPageAsync("Processes", _b.Id));

        Assert.Equal(_b.Id, shell.ActiveConnectionId);
        Assert.Equal(_b.Id, _settings.Load().ActiveConnectionId);
        Assert.Equal("Processes", shell.Current!.Tag);
        Assert.Equal(_b.Id, shell.WorkspaceServer);
        Assert.Same(shell.Servers.Single(s => s.Settings.Id == _b.Id), shell.Sidebar.Server);
        Assert.Equal("Processes", shell.Sidebar.Current);
        Assert.Equal("Activity", shell.Sidebar.OpenGroup);
        Assert.Contains("PROD-B", Breadcrumb, StringComparison.Ordinal);
        // The page reads PROD-B: the repository was built for it.
        Assert.Equal(_b.Id, _pages.Made[^1].ConnectionId);
        Assert.Equal("Server=prod-b", _repositories.Created[^1]);
    });

    [Fact]
    public void TheServerMenu_OpensTheSamePageOnAnotherServer_AndBackSwitchesBack() => OnDispatcher.Run(() =>
    {
        var shell = Started(_a, _b);
        UiThread.Wait(shell.OpenPageAsync("Waits", _a.Id));

        shell.TopBar.SwitchServer(_b.Id, current: _a.Id);
        UiThread.WaitUntil(() => shell.ActiveConnectionId == _b.Id && shell.Current?.ConnectionId == _b.Id);
        Assert.Equal("Waits", shell.Current!.Tag);
        Assert.Equal("Back to Waits · PROD-A (Alt+Left)", shell.TopBar.BackToolTip);

        UiThread.Wait(shell.GoAsync(back: true));
        Assert.Equal(_a.Id, shell.ActiveConnectionId);
        Assert.Equal(new Route("Waits", ConnectionId: _a.Id), shell.Current);
    });

    // ── Back and Forward ─────────────────────────────────────────────────

    [Fact]
    public void BackAndForward_GoThroughTheVisits_AndSayWhereTheyGo() => OnDispatcher.Run(() =>
    {
        var shell = Started(_a, _b);
        UiThread.Wait(shell.OpenPageAsync("Processes"));
        UiThread.Wait(shell.OpenPageAsync("Waits"));
        Assert.Equal("Processes · PROD-A", shell.TopBar.BackTo);
        Assert.Null(shell.TopBar.ForwardTo);
        Assert.False(shell.TopBar.ForwardCommand.CanExecute(null));

        UiThread.Wait(shell.GoAsync(back: true));
        Assert.Equal("Processes", shell.Current!.Tag);
        Assert.Equal("All servers", shell.TopBar.BackTo);
        Assert.Equal("Waits · PROD-A", shell.TopBar.ForwardTo);

        UiThread.Wait(shell.GoAsync(back: true));
        Assert.Equal("Home", shell.Current!.Tag);
        Assert.Null(shell.TopBar.BackTo);
        Assert.False(shell.TopBar.BackCommand.CanExecute(null));

        // The top bar's Forward goes the same way.
        shell.TopBar.ForwardCommand.Execute(null);
        UiThread.WaitUntil(() => shell.Current?.Tag == "Processes");
        Assert.True(shell.TopBar.BackCommand.CanExecute(null));
    });

    [Fact]
    public void LeavingAPage_KeepsHowItWasLookedAt_AndOpeningItAgainPutsItBack() => OnDispatcher.Run(() =>
    {
        var shell = Started(_a);
        UiThread.Wait(shell.OpenPageAsync("Processes"));
        Page.Kept = "the Waiting tab";
        _host.Calls.Clear();

        UiThread.Wait(shell.OpenPageAsync("Waits"));
        Assert.Equal("Keep Processes", _host.Calls[0]);

        UiThread.Wait(shell.GoAsync(back: true));
        Assert.Equal("the Waiting tab", Page.Restored);
        Assert.Equal(["Restore Processes", "Scroll Processes"], _host.Calls.TakeLast(2));
    });

    // ── The server's edition (#141) ──────────────────────────────────────

    [Fact]
    public void APageAzureSqlDatabaseHasNot_OpensTheServersOverview_AndItsButtonSaysWhy() => OnDispatcher.Run(() =>
    {
        _repositories.Azure.Add("prod-b");
        var shell = Started(_a, _b);
        Assert.Empty(shell.Sidebar.Unavailable);

        UiThread.Wait(shell.OpenPageAsync("AgentJobs", _b.Id));

        Assert.Equal(Workspace.Overview(_b.Id), shell.Current);
        Assert.Equal(ShellViewModel.PagesAzureSqlLacks.Keys.Order(), shell.Sidebar.Unavailable.Keys.Order());
        Assert.StartsWith("Not available on Azure SQL Database, which has no SQL Server Agent", shell.Sidebar.Unavailable["AgentJobs"], StringComparison.Ordinal);

        // Back on PROD-A, which has them all.
        UiThread.Wait(shell.OpenPageAsync("AgentJobs", _a.Id));
        Assert.Equal("AgentJobs", shell.Current!.Tag);
        Assert.Empty(shell.Sidebar.Unavailable);
    });

    // Code review: a new empty set on every navigation made the sidebar restyle its buttons each time.
    [Fact]
    public void TheSidebarHearsOfThePagesAServerLacks_OnlyWhenThatChanges() => OnDispatcher.Run(() =>
    {
        _repositories.Azure.Add("prod-b");
        var shell = Started(_a, _b);
        var raised = 0;
        shell.Sidebar.PropertyChanged += (_, e) => raised += e.PropertyName == nameof(SidebarViewModel.Unavailable) ? 1 : 0;

        UiThread.Wait(shell.OpenPageAsync("Processes", _a.Id));
        UiThread.Wait(shell.OpenPageAsync("Waits", _a.Id));
        Assert.Equal(0, raised);

        UiThread.Wait(shell.OpenPageAsync("Waits", _b.Id));
        UiThread.Wait(shell.OpenPageAsync("Processes", _b.Id));
        Assert.Equal(1, raised);
    });

    // Code review: the constructor built a repository Start threw away, and one that Shutdown would
    // have stopped a trace on, waiting up to 20 s, had the shell never started.
    [Fact]
    public void BeforeAWindowAttachesAndStartsIt_TheShellReadsNothing_AndShutsDownWithout() => OnDispatcher.Run(() =>
    {
        Directory.CreateDirectory(_dir);
        _settings = new ConnectionSettingsService(_dir);
        var monitoring = new MonitoringManager(new HistoryWriter(Path.Combine(_dir, "history.db"), (_, _) => { }),
                                               (_, _) => new FakeRepository { Unreachable = true });
        var shell = new ShellViewModel(new PageFactory(this), _dialogs, _settings, monitoring,
                                       new ChatNotificationService(new HttpWebhookSender(), "DBA-PC01", _ => { }),
                                       _repositories, new CollectorLink(new FakeCollectorControl(_dir), _settings, _ui, TimeProvider.System),
                                       new DirectWatchStarter(monitoring), _ui);

        Assert.False(shell.IsAttached);
        Assert.Throws<InvalidOperationException>(() => shell.Repo);
        shell.Shutdown(Engine.Monitoring.WatchStop.AppExit);
        Assert.Empty(_repositories.Created);

        shell.Attach(_host);
        Assert.True(shell.IsAttached);
    });

    // ── What the page on screen reports ──────────────────────────────────

    [Fact]
    public void OnlyThePageOnScreen_OwnsTheTopBar_AndOnlyItsFirstLoadFailingAsksAboutIt() => OnDispatcher.Run(() =>
    {
        var shell = Started(_a);
        UiThread.Wait(shell.OpenPageAsync("Processes"));
        var left = Page;
        UiThread.Wait(shell.OpenPageAsync("Waits"));

        shell.ShowError(left, Failure("boom"), PageRead.Load);
        Assert.Equal(string.Empty, shell.TopBar.Message);
        Assert.Empty(_dialogs.Shown);

        shell.ShowError(Page, Failure("Timeout expired"), PageRead.Timed);
        Assert.True(shell.TopBar.MessageIsError);
        Assert.Contains("Timeout expired", shell.TopBar.Message, StringComparison.Ordinal);
        Assert.Empty(_dialogs.Shown);

        shell.ShowError(Page, Failure("Login failed"), PageRead.Load);
        Assert.StartsWith("Warn: Data Error: Error tag:", Assert.Single(_dialogs.Shown), StringComparison.Ordinal);

        // A timed read that works keeps nothing of the error.
        shell.ShowRead(Page, PageRead.Timed);
        Assert.Equal(string.Empty, shell.TopBar.Message);
    });

    [Fact]
    public void Refresh_ReadsThePageAgain_AndIsOffWhileItReads() => OnDispatcher.Run(() =>
    {
        var shell = Started(_a);
        UiThread.Wait(shell.OpenPageAsync("Processes"));
        var page = Page;
        page.Gate = new TaskCompletionSource();

        shell.TopBar.RefreshCommand.Execute(null);
        Assert.False(shell.TopBar.CanRefresh);
        Assert.Equal("Refreshing…", shell.TopBar.Message);

        page.Gate.SetResult();
        UiThread.WaitUntil(() => shell.TopBar.CanRefresh);
        Assert.Equal(2, page.Loads);
        Assert.Equal(string.Empty, shell.TopBar.Message);
        Assert.Equal(PageKind.Snapshot, shell.TopBar.Freshness!.Kind);
        Assert.NotNull(shell.TopBar.Freshness.Taken);
    });

    [Fact]
    public void Pause_IsTheLivePagesOwn() => OnDispatcher.Run(() =>
    {
        _host.Context = new FakePageContext { Kind = PageKind.Live, CanPause = true };
        var shell = Started(_a);
        UiThread.Wait(shell.OpenPageAsync("LiveMetrics"));
        Assert.True(shell.TopBar.ShowsPause);

        shell.TopBar.PauseCommand.Execute(null);

        Assert.True(_host.Context.IsPaused);
        Assert.True(shell.TopBar.IsPaused);
        Assert.True(shell.TopBar.ShowsRefresh);
    });

    // ── Focus mode and the sidebar ───────────────────────────────────────

    [Fact]
    public void FocusMode_HidesTheSidebar_AndPinningItFromTheSlidInMenuEndsIt() => OnDispatcher.Run(() =>
    {
        var shell = Started(_a);

        shell.TopBar.ToggleFocusCommand.Execute(null);
        Assert.True(shell.InFocus);
        Assert.True(shell.TopBar.InFocus);
        Assert.Equal(SidebarMode.Hidden, shell.Sidebar.Shown);
        Assert.Equal("Show the menu", shell.TopBar.SidebarName);

        shell.Sidebar.Cycle();   // Ctrl+B: over the page
        Assert.True(shell.Sidebar.IsSlidOver);
        Assert.Equal("Close the menu", shell.TopBar.SidebarName);

        shell.Sidebar.Choose(SidebarMode.Pinned);
        Assert.False(shell.InFocus);
        Assert.Equal(SidebarMode.Pinned, shell.Sidebar.Shown);
        Assert.Equal(SidebarMode.Pinned, _settings.Load().SidebarMode);
    });

    [Fact]
    public void TheSidebarsCycle_IsSaved_AndTheMenuButtonSaysWhatComesNext() => OnDispatcher.Run(() =>
    {
        var shell = Started(_a);
        Assert.Equal("Collapse the menu", shell.TopBar.SidebarName);

        shell.TopBar.ToggleSidebarCommand.Execute(null);
        Assert.Equal(SidebarMode.Collapsed, _settings.Load().SidebarMode);
        Assert.Equal("Hide the menu", shell.TopBar.SidebarName);

        shell.TopBar.ToggleSidebarCommand.Execute(null);
        Assert.Equal(SidebarMode.Hidden, _settings.Load().SidebarMode);
        // Hidden, ☰ slides it in over the page rather than pinning it.
        shell.TopBar.ToggleSidebarCommand.Execute(null);
        Assert.True(shell.Sidebar.IsSlidOver);
        Assert.True(shell.Sidebar.SlideByKeyboard);
        Assert.Equal(SidebarMode.Hidden, _settings.Load().SidebarMode);
    });

    [Fact]
    public void ThePagesInTheSidebar_OpenOnTheServerWhoseWorkspaceIsOpen() => OnDispatcher.Run(() =>
    {
        var shell = Started(_a, _b);
        UiThread.Wait(shell.OpenPageAsync("Overview", _b.Id));

        shell.Sidebar.OpenPage("Deadlocks");

        UiThread.WaitUntil(() => shell.Current?.Tag == "Deadlocks");
        Assert.Equal(_b.Id, shell.Current!.ConnectionId);
        Assert.Equal(_b.Id, shell.ActiveConnectionId);
    });

    // ── The quick switcher (#144) ────────────────────────────────────────

    [Fact]
    public void TheQuickSwitcher_FindsTheOpenServersPages_AndOpensThePickedOne() => OnDispatcher.Run(() =>
    {
        var shell = Started(_a, _b);
        UiThread.Wait(shell.OpenPageAsync("Processes", _a.Id));
        var opening = 0;
        var intoPage = 0;
        shell.SwitcherOpening        += () => opening++;
        shell.FocusIntoPageRequested += () => intoPage++;

        shell.TopBar.JumpCommand.Execute(null);
        Assert.True(shell.Switcher.IsOpen);
        Assert.Equal(1, opening);
        // Inside a server's workspace with another saved: a server's name keeps the page.
        Assert.True(shell.Switcher.KeepsPage);

        shell.Switcher.Query = "tempdb";
        Assert.Equal("TempDB", shell.Switcher.Selected!.Text);
        Assert.True(shell.Switcher.Pick(shell.Switcher.Selected, intoPage: true));

        UiThread.WaitUntil(() => shell.Current?.Tag == "TempDb");
        Assert.False(shell.Switcher.IsOpen);
        Assert.Equal(_a.Id, shell.Current!.ConnectionId);
        Assert.Equal(1, intoPage);
        Assert.Equal("Processes · PROD-A", shell.TopBar.BackTo);
    });

    // ── The first page ───────────────────────────────────────────────────

    [Fact]
    public void WithNothingSaved_TheFirstPageIsTheFirstRunFlow() => OnDispatcher.Run(() =>
    {
        var shell = Started();

        Assert.Equal("Settings", shell.Current!.Tag);
        Assert.Equal(SettingsLocation.FirstRunFlow, _pages.Made[^1].Route.Settings);
    });

    [Fact]
    public void TheNavigator_OpensPagesThroughTheShell_AndAnUnknownTagIsABug() => OnDispatcher.Run(() =>
    {
        var shell = Started(_a);
        var navigator = new ShellNavigator(() => shell);

        UiThread.Wait(navigator.GoToAsync("Processes"));
        Assert.Equal("Processes", shell.Current!.Tag);
        UiThread.Wait(navigator.BackAsync());
        Assert.Equal("Overview", shell.Current!.Tag);

        Assert.Throws<ArgumentException>(() => { _ = navigator.GoToAsync("NoSuchPage"); });
        // Without a window, nothing happens.
        UiThread.Wait(new ShellNavigator(() => null).GoToAsync("Processes"));
    });

    // ── Fakes ────────────────────────────────────────────────────────────

    private static WaitStatsException Failure(string message) =>
        new("ProcessRepository", "GetProcessesAsync", new InvalidOperationException(message));

    /// <summary>A page's view model that counts its loads, keeps what it is told to, and can be held.</summary>
    private sealed class StubPage : PageViewModel
    {
        public int Loads;
        public object? Kept, Restored;
        public TaskCompletionSource? Gate;

        protected override Task LoadAsync(CancellationToken cancel)
        {
            Loads++;
            return Gate?.Task ?? Task.CompletedTask;
        }

        public override object? SaveState() => Kept;

        public override void RestoreState(object state) => Restored = state;
    }

    /// <summary>
    /// Makes a stub view model for every tag, reporting to the shell as the app's do. The shell never
    /// touches the view, so no page is made.
    /// </summary>
    private sealed class PageFactory(ShellViewModelTests test) : IPageFactory
    {
        public List<PageArgs> Made { get; } = [];

        public IReadOnlyCollection<string> Tags => [.. PageCatalog.Types.Keys];

        public PageInstance Create(string tag, PageArgs args)
        {
            Made.Add(args);
            var model = new StubPage();
            model.Connect(tag, new WpfShellStatus(() => test._shell, NullLogger<WpfShellStatus>.Instance),
                          new RefreshScheduler(TimeProvider.System, test._ui));
            return new PageInstance(null!, model);
        }
    }

    /// <summary>A repository per server; the servers named in <see cref="Azure"/> are Azure SQL Databases.</summary>
    private sealed class Repositories : IRepositoryFactory
    {
        public HashSet<string> Azure { get; } = [];

        /// <summary>The connection string of each repository made, in order.</summary>
        public List<string> Created { get; } = [];

        public SqlTarget TargetFor(ConnectionSettings? connection, int commandTimeoutSeconds) =>
            new(connection is null ? string.Empty : $"Server={connection.Server}");

        public IWaitStatsRepository Create(SqlTarget target)
        {
            Created.Add(target.ConnectionString);
            var server = target.ConnectionString.Replace("Server=", string.Empty, StringComparison.Ordinal);
            return new FakeRepository { IsAzureSqlDatabaseAsync = () => Task.FromResult(Azure.Contains(server)) };
        }
    }
}
