using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Pages.Settings;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Permissions;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The first-run flow (#177) and a card's Check permissions, as WPF builds them off screen, bound to
/// their view models (#190). The permission check is a fake: no server is contacted, and Connect's
/// save is played by saving the connection and telling the flow, since saving signs in first.
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class FirstRunTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly FakeClipboard _clipboard = new();
    private readonly RecordingStarter _starter = new();
    private readonly List<Guid> _opened = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly List<ConnectionSettings> _checked = new();

    private MonitoringManager?         _monitoring;
    private ConnectionSettingsService? _settings;

    // What the fake check answers; a bare login on SQL Server unless a test says otherwise.
    private Func<PermissionProbe> _answer = () => Bare();

    private static PermissionProbe Bare(int edition = 3) =>
        new(edition, "CONTOSO\\svc_vitals", "svc_vitals", "Sales", LoginIsOwn: true, ServerState: false, AnyDefinition: false,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static PermissionProbe Everything() =>
        Bare() with
        {
            ServerState = true, AnyDefinition = true,
            MsdbReadable = PermissionCheck.BackupTables.Concat(PermissionCheck.JobTables).ToHashSet(StringComparer.OrdinalIgnoreCase),
        };

    private static ConnectionSettings Conn(string name) => new()
    {
        Server = "sql01", Name = name, Database = "Sales", Authentication = SqlAuthMode.Windows,
    };

    // On the UI thread: Settings with the saved connections, opened at the location.
    private SettingsPage Open(SettingsLocation location, params ConnectionSettings[] saved)
    {
        _settings = new ConnectionSettingsService(_dir);
        foreach (var conn in saved)
            _settings.Upsert(conn);
        _monitoring = new MonitoringManager(new HistoryWriter(Path.Combine(_dir, "history.db"), (_, _) => { }),
                                            (_, _) => new FakeRepository());
        _monitoring.Sync(_settings.Load(), ActivitySources.AutomaticAtStartup);
        var chat = new ChatNotificationService(new NoSender(), "DBA-PC01", _ => { });

        var context = new SettingsContext(_settings, _monitoring, chat, _ => Task.CompletedTask, _dialogs,
                                          checkPermissions: Check, starter: _starter, clipboard: _clipboard)
        {
            Theme = new FakeThemeService(),
        };
        var page = new SettingsPage(new SettingsViewModel(context, location));
        page.ViewModel.ServerRequested += _opened.Add;
        UiThread.Layout(page, 1200, 900);
        return page;
    }

    private Task<PermissionProbe> Check(ConnectionSettings connection, CancellationToken cancellationToken)
    {
        _checked.Add(connection);
        return Task.Run(_answer, cancellationToken);
    }

    // Connect's Save & continue, without signing in: the connection saved, then the flow told.
    private ConnectionSettings Connect(FirstRunFlow flow)
    {
        var conn = Conn("Sales DB");
        _settings!.Upsert(conn);
        _settings.SetActive(conn.Id, ActivitySources.Settings);
        flow.ViewModel.Connected(conn.Id);
        return conn;
    }

    private static void WaitForCheck(PermissionCheckViewModel model) =>
        UiThread.WaitUntil(() => model.State is PermissionCheckState.Done or PermissionCheckState.Failed);

    // ── Connect ──────────────────────────────────────────────────────────────

    [Fact]
    public void WithNothingSaved_SettingsOpensOnConnect_TheNewConnectionsEditorForTheFirstRun() => UiThread.Run(() =>
    {
        var page = Open(SettingsLocation.FirstRunFlow);

        var flow = Assert.IsType<FirstRunFlow>(page.FirstRun);
        Assert.Equal(FirstRunStep.Connect, flow.ViewModel.Step);
        Assert.True(flow.Editor.ViewModel.IsNew);
        Assert.Equal(Visibility.Visible, page.FlowHost.Visibility);
        // No way back to the cards, and no Save & make active: Save & continue is the way on.
        Assert.Equal(Visibility.Collapsed, flow.Editor.Crumb.Visibility);
        Assert.Equal(Visibility.Collapsed, flow.Editor.BtnSaveAndConnect.Visibility);
        Assert.Equal(Visibility.Collapsed, flow.Editor.BtnSave.Visibility);
        Assert.Equal("Save & continue", flow.Editor.BtnSaveAndContinue.Content);
        Assert.Contains("Save & continue", flow.Editor.TxtLine.Text, StringComparison.Ordinal);
        Assert.Equal("Step 1 of 3: Connect", System.Windows.Automation.AutomationProperties.GetName(flow.Steps));
    });

    [Fact]
    public void WithAConnectionSaved_TheFlowDoesntShow() => UiThread.Run(() =>
    {
        var page = Open(SettingsLocation.FirstRunFlow, Conn("Sales DB"));

        Assert.Null(page.FirstRun);
        Assert.Equal(Visibility.Collapsed, page.FlowHost.Visibility);
        Assert.Single(page.ViewModel.Connections!.Cards);
    });

    [Fact]
    public void Settings_OpenedAnyOtherWay_HasNoFlow() => UiThread.Run(() =>
    {
        var page = Open(new SettingsLocation(SettingsCategory.Connections));

        Assert.Null(page.FirstRun);
    });

    // ── Check ────────────────────────────────────────────────────────────────

    [Fact]
    public void Saved_ChecksTheConnection_ListsWhatsMissing_AndOffersTheGrantToCopy() => UiThread.Run(() =>
    {
        var page = Open(SettingsLocation.FirstRunFlow);
        var flow = page.FirstRun!;

        var conn = Connect(flow);
        Assert.Equal(FirstRunStep.Check, flow.ViewModel.Step);
        var model = flow.Check!.Model;
        WaitForCheck(model);
        UiThread.Layout(page, 1200, 900);

        Assert.Equal(conn.Id, Assert.Single(_checked).Id);
        Assert.Equal(PermissionCheckState.Done, model.State);
        Assert.Equal("4 of 4 permissions are missing.", model.Status);
        Assert.Equal(4, model.Rows.Count);
        Assert.All(model.Rows, r => Assert.Equal("✗  Missing", r.StateText));
        Assert.Contains("Overview", model.Rows[0].Affects, StringComparison.Ordinal);
        Assert.Contains("Monitoring and the health score", model.Rows[0].Affects, StringComparison.Ordinal);
        Assert.Contains("GRANT VIEW SERVER STATE TO [CONTOSO\\svc_vitals];", model.Script, StringComparison.Ordinal);
        Assert.Equal(Visibility.Visible, flow.Check.ScriptPanel.Visibility);
        Assert.Equal(model.Script, flow.Check.TxtScript.Text);
        Assert.Equal(4, UiThread.Find<ContentPresenter>(flow.Check.Rows).Count(p => p.Content is PermissionRow));

        flow.Check.BtnCopy.Command.Execute(null);
        Assert.Equal(model.Script, _clipboard.Text);
        Assert.Equal("✓  Copied", model.CopyText);
    });

    [Fact]
    public void EverythingGranted_SaysSo_AndHasNoScript() => UiThread.Run(() =>
    {
        _answer = Everything;
        var page = Open(SettingsLocation.FirstRunFlow);
        Connect(page.FirstRun!);
        var check = page.FirstRun!.Check!;
        WaitForCheck(check.Model);
        UiThread.Layout(page, 1200, 900);

        Assert.Equal("All 4 permissions SqlVitals reads with are there.", check.Model.Status);
        Assert.False(check.Model.HasScript);
        Assert.Equal(Visibility.Collapsed, check.ScriptPanel.Visibility);
        Assert.Equal(((SolidColorBrush)Application.Current.Resources["Success"]).Color, ((SolidColorBrush)check.TxtStatus.Foreground).Color);
    });

    [Fact]
    public void AFailedSignIn_SaysWhy_InTheServersWords() => UiThread.Run(() =>
    {
        _answer = () => throw new WaitStatsException("ServerRepository", "CheckPermissionsAsync",
                                                     new InvalidOperationException("Login failed for user 'app'. Password=hunter2;"));
        var page = Open(SettingsLocation.FirstRunFlow);
        Connect(page.FirstRun!);
        var model = page.FirstRun!.Check!.Model;
        WaitForCheck(model);

        Assert.Equal(PermissionCheckState.Failed, model.State);
        Assert.StartsWith("Couldn't check: Login failed for user 'app'.", model.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("ServerRepository", model.Status, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", model.Status, StringComparison.Ordinal);
        Assert.Empty(model.Rows);
        Assert.Equal("Check again", model.CheckText);
    });

    [Fact]
    public void CheckAgain_AsksTheServerAgain_AndShowsAGrantMadeSince() => UiThread.Run(() =>
    {
        var page = Open(SettingsLocation.FirstRunFlow);
        Connect(page.FirstRun!);
        var model = page.FirstRun!.Check!.Model;
        WaitForCheck(model);

        _answer = Everything;
        model.CheckCommand.Execute(null);
        UiThread.WaitUntil(() => model.Report?.AllGranted == true);

        Assert.Equal(2, _checked.Count);
        Assert.False(model.HasScript);
    });

    [Fact]
    public void AzureSqlDatabase_IsAskedForViewDatabaseStateOnly() => UiThread.Run(() =>
    {
        _answer = () => Bare(ServerCapabilities.AzureSqlDatabaseEdition);
        var page = Open(SettingsLocation.FirstRunFlow);
        Connect(page.FirstRun!);
        var model = page.FirstRun!.Check!.Model;
        WaitForCheck(model);

        Assert.Equal(["✗  Missing", "Not needed", "Not needed", "Not needed"], model.Rows.Select(r => r.StateText));
        Assert.Equal("", model.Rows[1].Affects);
        Assert.Contains("GRANT VIEW DATABASE STATE TO [svc_vitals];", model.Script, StringComparison.Ordinal);
        Assert.StartsWith("Ask an admin of Sales", model.ScriptIntro, StringComparison.Ordinal);
    });

    // ── Start ────────────────────────────────────────────────────────────────

    [Fact]
    public void Start_MonitorsByDefault_ThenOpensTheServersOverview() => UiThread.Run(() =>
    {
        var page = Open(SettingsLocation.FirstRunFlow);
        var flow = page.FirstRun!;
        var conn = Connect(flow);
        WaitForCheck(flow.Check!.Model);

        flow.BtnNext.Command.Execute(null);
        Assert.Equal(FirstRunStep.Start, flow.ViewModel.Step);
        Assert.True(flow.ChkStartMonitoring.IsChecked);
        Assert.Equal("Open Sales DB's Overview  ›", flow.BtnFinish.Content);

        UiThread.Wait(flow.ViewModel.FinishAsync());

        Assert.Equal((conn.Id, ActivitySources.FirstRun), Assert.Single(_starter.Started));
        Assert.Equal(conn.Id, Assert.Single(_opened));
        Assert.Null(page.FirstRun);
        Assert.Equal(Visibility.Collapsed, page.FlowHost.Visibility);
    });

    [Fact]
    public void Start_Unticked_OnlyOpensTheOverview() => UiThread.Run(() =>
    {
        var page = Open(SettingsLocation.FirstRunFlow);
        var flow = page.FirstRun!;
        var conn = Connect(flow);
        flow.ViewModel.Show(FirstRunStep.Start);
        flow.ChkStartMonitoring.IsChecked = false;

        UiThread.Wait(flow.ViewModel.FinishAsync());

        Assert.Empty(_starter.Started);
        Assert.Equal(conn.Id, Assert.Single(_opened));
    });

    // ── Skip ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Skip_WithNothingTyped_GoesStraightToConnections() => UiThread.Run(() =>
    {
        var page = Open(SettingsLocation.FirstRunFlow);

        UiThread.Wait(page.FirstRun!.ViewModel.SkipAsync());

        Assert.Empty(_dialogs.Shown);
        Assert.Null(page.FirstRun);
        Assert.Equal(Visibility.Visible, page.Connections!.EmptyState.Visibility);
    });

    [Fact]
    public void Skip_WithSomethingTyped_AsksFirst_AndCancelStays() => UiThread.Run(() =>
    {
        var page = Open(SettingsLocation.FirstRunFlow);
        var flow = page.FirstRun!;
        flow.Editor.TxtServer.Text = "sql01";

        UiThread.Wait(flow.ViewModel.SkipAsync());

        Assert.Single(_dialogs.Shown);
        Assert.Same(flow, page.FirstRun);

        _dialogs.SaveAnswer = DialogAnswer.No;
        UiThread.Wait(flow.ViewModel.SkipAsync());
        Assert.Null(page.FirstRun);
    });

    [Fact]
    public void Skip_AfterSaving_LeavesTheConnectionSaved() => UiThread.Run(() =>
    {
        var page = Open(SettingsLocation.FirstRunFlow);
        Connect(page.FirstRun!);

        UiThread.Wait(page.FirstRun!.ViewModel.SkipAsync());

        Assert.Null(page.FirstRun);
        Assert.Single(page.ViewModel.Connections!.Cards);
        Assert.Empty(_opened);
    });

    [Fact]
    public void LeavingSettingsInConnect_AsksAboutWhatsTyped() => UiThread.Run(() =>
    {
        var page = Open(SettingsLocation.FirstRunFlow);
        page.FirstRun!.Editor.TxtServer.Text = "sql01";

        Assert.False(UiThreadResult(page.ViewModel.TryLeaveAsync()));
        Assert.Single(_dialogs.Shown);
    });

    // ── Check permissions on a card ──────────────────────────────────────────

    [Fact]
    public void ACardsCheckPermissions_OpensTheCheck_AndBackGoesToTheCards() => UiThread.Run(() =>
    {
        var conn = Conn("Sales DB");
        var page = Open(new SettingsLocation(SettingsCategory.Connections), conn);
        var connections = page.Connections!;
        var card = Assert.Single(connections.ViewModel.Cards);
        Assert.True(card.CanCheckPermissions);
        Assert.Equal("Check the permissions of Sales DB", card.CheckPermissionsName);

        card.CheckPermissionsCommand!.Execute(null);
        var check = Assert.IsType<ConnectionPermissions>(connections.Permissions);
        WaitForCheck(check.Check.Model);
        UiThread.Layout(page, 1200, 900);

        Assert.Equal(conn.Id, Assert.Single(_checked).Id);
        Assert.Equal("Permissions of Sales DB", check.TxtTitle.Text);
        Assert.Equal("4 of 4 permissions are missing.", check.Check.Model.Status);

        Assert.Same(check.ViewModel.BackCommand, check.LnkBack.Command);
        check.ViewModel.BackCommand.Execute(null);
        Assert.Null(connections.Permissions);
        Assert.Equal(Visibility.Visible, connections.ListView.Visibility);
    });

    [Fact]
    public void WithoutAChecker_TheCardHasNoCheckPermissions() => UiThread.Run(() =>
    {
        var item = new ConnectionCardItem(Engine.Settings.ConnectionCard.From(
            ViewModels.Settings.ConnectionsCategoryViewModel.InputFor(Conn("A"), new ConnectionStore(), new MonitoringManager(
                new HistoryWriter(Path.Combine(_dir, "h.db"), (_, _) => { }), (_, _) => new FakeRepository()))));

        Assert.False(item.CanCheckPermissions);
    });

    // ── The pages each permission affects are the sidebar's ──────────────────

    [Fact]
    public void ThePagesNamed_AreSidebarPages()
    {
        var titles = NavMenu.AllPages.Select(p => p.Label).ToHashSet();
        var results = PermissionCheck.Evaluate(Bare()).Results;

        Assert.All(results.SelectMany(r => r.Pages), page => Assert.Contains(page, titles));
    }

    // ── Render: each step, both themes ───────────────────────────────────────

    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    [Theory]
    [MemberData(nameof(Themes))]
    public void EachStep_Renders(string theme) => UiThread.Run(() =>
    {
        using var themed = Themed(theme);
        using var errors = BindingErrors.Watch();
        _answer = () => Everything() with { AnyDefinition = false, MsdbReadable = new HashSet<string>(PermissionCheck.BackupTables) };
        var page = Open(SettingsLocation.FirstRunFlow);
        var flow = page.FirstRun!;

        // The step being done is on the accent, those to come are muted.
        Render(page, theme, "1-connect");
        Assert.Equal(Brush("Accent"), ((SolidColorBrush)flow.Dot1.Background).Color);
        Assert.Equal(Brush("BgCard"), ((SolidColorBrush)flow.Dot2.Background).Color);

        Connect(flow);
        WaitForCheck(flow.Check!.Model);
        UiThread.Layout(page, 1200, 900);
        Render(page, theme, "2-check");
        Assert.Equal("✓", flow.Num1.Text);
        Assert.Equal(Brush("Accent"), ((SolidColorBrush)flow.Dot2.Background).Color);
        var missing = UiThread.Find<TextBlock>(flow.Check.Rows).First(t => t.Text == "✗  Missing");
        Assert.Equal(Brush("Danger"), ((SolidColorBrush)missing.Foreground).Color);
        var granted = UiThread.Find<TextBlock>(flow.Check.Rows).First(t => t.Text == "✓  Granted");
        Assert.Equal(Brush("Success"), ((SolidColorBrush)granted.Foreground).Color);
        Assert.Equal(Brush("Warning"), ((SolidColorBrush)flow.Check.TxtStatus.Foreground).Color);

        flow.ViewModel.Show(FirstRunStep.Start);
        UiThread.Layout(page, 1200, 900);
        Render(page, theme, "3-start");
        Assert.True(flow.BtnFinish.ActualWidth > 0);
        errors.AssertNone();
    });

    [Theory]
    [MemberData(nameof(Themes))]
    public void TheCardsCheck_Renders(string theme) => UiThread.Run(() =>
    {
        using var themed = Themed(theme);
        using var errors = BindingErrors.Watch();
        var page = Open(new SettingsLocation(SettingsCategory.Connections), Conn("Sales DB"));
        UiThread.Layout(page, 1200, 900);
        Render(page, theme, "cards");

        page.ViewModel.Connections!.Cards[0].CheckPermissionsCommand!.Execute(null);
        WaitForCheck(page.Connections!.Permissions!.Check.Model);
        UiThread.Layout(page, 1200, 900);
        Render(page, theme, "card-check");
        Assert.True(page.Connections.Permissions.Check.TxtScript.ActualHeight > 0);
        errors.AssertNone();
    });

    private static Color Brush(string key) => ((SolidColorBrush)Application.Current.Resources[key]).Color;

    private static bool UiThreadResult(Task<bool> task)
    {
        UiThread.Wait(task);
        return task.Result;
    }

    // The app's theme swapped for one test, and put back after it.
    private static IDisposable Themed(string theme)
    {
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var before = dictionaries.ToList();
        dictionaries.Clear();
        dictionaries.Add((ResourceDictionary)Application.LoadComponent(new Uri($"/SqlVitals.Desktop;component/Styles/{theme}.xaml", UriKind.Relative)));
        return new Restore(() =>
        {
            dictionaries.Clear();
            foreach (var dictionary in before)
                dictionaries.Add(dictionary);
        });
    }

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    // To look at: %TEMP%\SqlVitalsTests\first-run\<name>-<theme>.png.
    private static void Render(FrameworkElement element, string theme, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", "first-run");
        Directory.CreateDirectory(dir);
        var bitmap = new RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var back = new DrawingVisual();
        using (var context = back.RenderOpen())
            context.DrawRectangle((Brush)Application.Current.Resources["BgDeep"], null, new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        bitmap.Render(back);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(dir, $"{name}-{theme}.png"));
        encoder.Save(file);
    }

    /// <summary>Start, recorded: the app's may ask about the collector service.</summary>
    private sealed class RecordingStarter : IWatchStarter
    {
        public List<(Guid, string)> Started { get; } = new();

        public Task StartAsync(Guid connectionId, string source)
        {
            Started.Add((connectionId, source));
            return Task.CompletedTask;
        }
    }

    private sealed class NoSender : IWebhookSender
    {
        public Task<WebhookResult> SendAsync(Uri url, string json, ChatService service, CancellationToken cancel = default) =>
            throw new InvalidOperationException("No chat in these tests.");

        public Task<WebhookResult> PingAsync(Uri url, CancellationToken cancel = default) =>
            throw new InvalidOperationException("No chat in these tests.");
    }

    public void Dispose()
    {
        if (_monitoring is { } monitoring)
            UiThread.Run(monitoring.Dispose);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
