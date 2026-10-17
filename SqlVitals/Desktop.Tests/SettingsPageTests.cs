using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Pages.Settings;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Desktop.ViewModels.Settings;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Settings;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Settings (#131) as WPF builds it, off screen, bound to its view models (#190), over the three
/// connections of the Home tests: "Hot" (active, CPU at 99 %, so critical), "Down" (monitored in the
/// background, unreachable) and "Quiet" (not monitored). No server is ever contacted: nothing here
/// changes how a connection connects, so saving doesn't sign in first. What the view models decide
/// on their own is in their tests (<see cref="ConnectionEditorViewModelTests"/>, <see cref="SettingsViewModelTests"/>).
/// </summary>
[Collection(PresentationSourceCollection.Name)]
public sealed class SettingsPageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Server, string Method)> _calls = new();
    private readonly List<bool> _applied = new();
    private readonly FakeDialogService _dialogs = new();
    private readonly List<GridDensity> _densities = new();

    private readonly ConnectionSettings _hot   = Conn("hot",   "Hot");
    private readonly ConnectionSettings _down  = Conn("down",  "Down", background: true);
    private readonly ConnectionSettings _quiet = Conn("quiet", "Quiet");

    private MonitoringManager?         _monitoring;
    private ConnectionSettingsService? _settings;
    private ChatNotificationService?   _chat;

    // Run as MainWindow applies a save, e.g. to try leaving Settings as opening Live Metrics does.
    private Func<Task>? _onApply;

    // The collector service (#172): installed when a test sets it.
    private FakeCollectorControl? _control;
    private CollectorLink?        _collectors;

    private static ConnectionSettings Conn(string server, string name, bool background = false) => new()
    {
        Server              = server,
        Name                = name,
        Database            = "Sales",
        Authentication      = SqlAuthMode.Windows,
        MonitorInBackground = background,
    };

    private IWaitStatsRepository Repository(ConnectionSettings conn) => conn.Server == "hot"
        ? MonitoringManagerTests.Hot(conn.Server, _calls)
        : MonitoringManagerTests.Counting(conn.Server, _calls);

    // On the UI thread: the three connections saved, Hot active, monitoring started automatically,
    // one sample from each monitored connection, and the page opened at the location.
    private SettingsPage Open(SettingsLocation? location = null)
    {
        _settings = new ConnectionSettingsService(_dir);
        foreach (var conn in new[] { _hot, _down, _quiet })
            _settings.Upsert(conn);
        _settings.SetActive(_hot.Id);
        _settings.SetStartMonitoringAutomatically(true);

        _monitoring = new MonitoringManager(new HistoryWriter(Path.Combine(_dir, "history.db"), (_, _) => { }),
                                            (conn, _) => Repository(conn));
        _monitoring.Sync(_settings.Load(), ActivitySources.AutomaticAtStartup);
        UiThread.Wait(_monitoring.Get(_hot.Id)!.CollectNowAsync());
        MonitoringManagerTests.WaitForServerChecks(_monitoring.Get(_hot.Id)!);
        try { UiThread.Wait(_monitoring.Get(_down.Id)!.CollectNowAsync()); }
        catch (InvalidOperationException) { /* "No server in tests.": Down can't be reached */ }

        _chat = new ChatNotificationService(new NoSender(), "DBA-PC01", _ => { });
        _chat.Apply(_settings.Load());

        if (_control is not null)
        {
            _collectors = new CollectorLink(_control, _settings, new FakeUiDispatcher(), TimeProvider.System);
            UiThread.Wait(_collectors.RefreshAsync());
        }
        var page = new SettingsPage(new SettingsViewModel(TestContext(), location));
        UiThread.Layout(page, 1200, 900);
        return page;
    }

    private SettingsContext TestContext() =>
        new(_settings!, _monitoring!, _chat!, Apply, _dialogs, _collectors)
        {
            Theme        = new FakeThemeService(),
            // Recorded, not applied: the app's resources are every test's.
            ApplyDensity = _densities.Add,
        };

    // What MainWindow does after a save: monitoring follows the settings.
    private Task Apply(bool connect)
    {
        _applied.Add(connect);
        _monitoring!.Sync(_settings!.Load(), ActivitySources.Settings);
        return _onApply?.Invoke() ?? Task.CompletedTask;
    }

    // Lets the dispatcher run until the task is done, then its result.
    private static T Await<T>(Task<T> task)
    {
        UiThread.Wait(task);
        return task.Result;
    }

    private static ConnectionEditorViewModel Editor(SettingsPage page) => page.ViewModel.Connections!.Editor!;

    private static ConnectionEditor EditorView(SettingsPage page) => page.Connections!.Editor!;

    private static T Category<T>(SettingsPage page) where T : SettingsCategoryViewModel =>
        Assert.IsType<T>(page.ViewModel.Current);

    private IReadOnlyList<string> Asked => _dialogs.Shown;

    private int CountChanges(Action action)
    {
        var writes = 0;
        void Count(IReadOnlyList<ActivityEntry> _) => writes++;
        _settings!.Changed += Count;
        try { action(); }
        finally { _settings.Changed -= Count; }
        return writes;
    }

    private static RadioButton MenuButton(SettingsPage page, string title) =>
        UiThread.Find<RadioButton>(page.Menu).Single(r => System.Windows.Automation.AutomationProperties.GetName(r).StartsWith(title, StringComparison.Ordinal));

    // ── The side menu ─────────────────────────────────────────────────────────

    [Fact]
    public void OpensOnConnections_AndTheSideMenuSwitchesCategory_BuildingEachWhenFirstOpened() => UiThread.Run(() =>
    {
        var page = Open();

        Assert.Equal(SettingsCategory.Connections, page.Category);
        Assert.IsType<ConnectionsCategory>(page.Shown);
        Assert.True(page.ViewModel.MenuItemFor(SettingsCategory.Connections).IsSelected);
        Assert.True(MenuButton(page, "Connections").IsChecked);
        Assert.Equal("Connections, 3 saved", page.ViewModel.MenuItemFor(SettingsCategory.Connections).AutomationName);

        var shown = new List<SettingsCategory>();
        page.ViewModel.CategoryShown += shown.Add;
        TemplateRolloutTests.Click(MenuButton(page, "Alerts"));
        UiThread.Layout(page, 1200, 900);

        Assert.Equal(SettingsCategory.Alerts, page.Category);
        Assert.IsType<AlertsCategory>(page.Shown);
        Assert.True(MenuButton(page, "Alerts").IsChecked);
        Assert.False(MenuButton(page, "Connections").IsChecked);
        Assert.Equal([SettingsCategory.Alerts], shown);

        // The same instance when it's opened again: what's typed in it stays.
        var first = page.Shown;
        UiThread.Wait(page.OpenAsync(new SettingsLocation(SettingsCategory.Appearance)));
        UiThread.Wait(page.OpenAsync(new SettingsLocation(SettingsCategory.Alerts)));
        Assert.Same(first, page.Shown);
    });

    [Fact]
    public void TheCollectorService_NotInstalled_SaysHowToGetIt_AndNothingCanBeHandedOver() => UiThread.Run(() =>
    {
        var page = Open(new SettingsLocation(SettingsCategory.Collector));
        var collector = Category<CollectorCategoryViewModel>(page);
        var view = Assert.IsType<CollectorCategory>(page.Shown);

        Assert.StartsWith("Not installed. SqlVitals watches your servers itself while it runs.", view.TxtState.Text);
        Assert.False(view.BtnStart.IsEnabled);
        Assert.Equal(["Down", "Hot", "Quiet"], collector.Rows.Select(r => r.Name));
        Assert.All(collector.Rows, r => Assert.False(r.CanAct));
        Assert.All(collector.Rows, r => Assert.Equal("The collector service isn't installed on this PC.", r.ActionToolTip));
        Assert.Equal(0, view.CmbChoice.SelectedIndex);
    });

    [Fact]
    public void TheCollectorService_HandsAServerOver_AndTakesItBack() => UiThread.Run(() =>
    {
        Directory.CreateDirectory(Path.Combine(_dir, "collector"));
        _control = new FakeCollectorControl(Path.Combine(_dir, "collector")) { State = CollectorServiceState.Running };
        var page = Open(new SettingsLocation(SettingsCategory.Collector));
        var collector = Category<CollectorCategoryViewModel>(page);
        var view = Assert.IsType<CollectorCategory>(page.Shown);
        Assert.StartsWith("Running. It watches 0 of your servers", collector.StateText);
        Assert.False(view.BtnStart.IsEnabled);
        Assert.True(view.BtnStop.IsEnabled);

        var hot = collector.Rows.Single(r => r.Name == "Hot");
        Assert.Equal(("Hand over", true, "Watched in SqlVitals."), (hot.ActionText, hot.CanAct, hot.Where));
        hot.Act.Execute(null);
        UiThread.WaitUntil(() => collector.Rows.Single(r => r.Name == "Hot").ActionText == "Take back");

        Assert.Equal(_hot.Id, Assert.Single(_control.Saved().Servers).Id);
        Assert.StartsWith("✓  Handed Hot to the service.", collector.ServersStatus.Text);
        Assert.StartsWith("Running. It watches 1 of your servers", collector.StateText);

        collector.Rows.Single(r => r.Name == "Hot").Act.Execute(null);
        UiThread.WaitUntil(() => collector.Rows.Single(r => r.Name == "Hot").ActionText == "Hand over");
        Assert.Empty(_control.Saved().Servers);

        // What Start does is saved as it's picked, and logged as a setting.
        view.CmbChoice.SelectedIndex = (int)WatchChoice.Service;
        Assert.Equal(WatchChoice.Service, _settings!.Load().WatchChoice);
        Assert.Equal(WatchChoice.Service, collector.WatchChoice);
    });

    [Fact]
    public void TheChatChannels_SayPlainly_ThatAlertsStopWhenThePcSleepsOrTheAppCloses() => UiThread.Run(() =>
    {
        var page = Open(new SettingsLocation(SettingsCategory.Alerts));
        var alerts = Assert.IsType<AlertsCategory>(page.Shown);

        // On the screen itself (#170), not only in the wiki, and whether or not there are channels.
        Assert.Equal(Visibility.Visible, alerts.ChatStopsWarning.Visibility);
        Assert.StartsWith("⚠  Alerts stop when this PC sleeps, shuts down, or SqlVitals is closed", alerts.TxtChatStopsWarning.Text);
    });

    [Fact]
    public void AChannelsHeartbeat_IsChosenInItsForm_WithItsPingUrlMaskedOnceSaved() => UiThread.Run(() =>
    {
        const string ping = "https://hc-ping.com/0f3b8a5e-1c2d-4e5f-8a9b-0c1d2e3f4a5b";
        var page = Open(new SettingsLocation(SettingsCategory.Alerts));
        var alerts = Assert.IsType<AlertsCategory>(page.Shown);
        var model  = alerts.ViewModel;
        TemplateRolloutTests.Click(alerts.BtnAddChatChannel);
        UiThread.Layout(page, 1200, 900);

        // Typed in the boxes, so the bindings are what's tested.
        var form = model.Form!;
        Assert.Equal(Visibility.Visible, alerts.ChatChannelForm.Visibility);
        alerts.TxtChatName.Text = "DBA team";
        alerts.TxtChatUrl.Text  = "https://prod-27.westeurope.logic.azure.com:443/workflows/0a1b/triggers/manual/paths/invoke?sig=AbCd";
        Assert.Equal(ChatService.Teams, form.Service);

        // Daily at shows the time; the steps follow the service and the schedule.
        alerts.CmbHeartbeat.SelectedIndex = 8;
        UiThread.Layout(page, 1200, 900);
        Assert.Equal(Visibility.Visible, alerts.CmbHeartbeatAt.Visibility);
        alerts.CmbHeartbeatAt.SelectedIndex = 19; // 09:30
        Assert.Equal(HeartbeatSchedule.DailyAt(new TimeSpan(9, 30, 0)), form.ChosenHeartbeat());
        alerts.ChkHeartbeatSteps.IsChecked = true;
        UiThread.Layout(page, 1200, 900);
        Assert.Equal(Visibility.Visible, alerts.PnlHeartbeatSteps.Visibility);
        Assert.Contains("Recurrence: every day, starting 10:00", alerts.TxtHeartbeatSteps.Text);

        // A ping URL that isn't https blocks the save, and says why.
        alerts.TxtPingUrl.Text = "http://hc-ping.com/abc";
        TemplateRolloutTests.Click(alerts.BtnSaveChatChannel);
        Assert.Contains("must start with https://", alerts.TxtChatFormMessage.Text);
        Assert.Empty(_settings!.Load().ChatChannels);

        alerts.TxtPingUrl.Text = ping;
        TemplateRolloutTests.Click(alerts.BtnSaveChatChannel);
        UiThread.Layout(page, 1200, 900);

        var saved = Assert.Single(_settings.Load().ChatChannels);
        Assert.Equal((HeartbeatEvery.Daily, new TimeSpan(9, 30, 0), ping), (saved.HeartbeatEvery, saved.HeartbeatAt, saved.HeartbeatPingUrl));
        // Opened again as saved: the URLs are in no control until Show.
        Assert.NotSame(form, model.Form);
        Assert.Equal(string.Empty, alerts.TxtPingUrl.Text);
        Assert.Equal(string.Empty, alerts.TxtChatUrl.Text);
        Assert.StartsWith("hc-ping.com/…/4a5b", alerts.TxtPingUrlMasked.Text);
        // The webhook's Show shows both secrets.
        TemplateRolloutTests.Click(alerts.BtnShowChatUrl);
        Assert.Equal(ping, alerts.TxtPingUrl.Text);
        Assert.Equal(saved.WebhookUrl, alerts.TxtChatUrl.Text);
        // Copy steps is a bound command (the real clipboard is left alone here).
        Assert.Same(model.Form!.CopyStepsCommand, alerts.BtnCopyHeartbeatSteps.Command);
        Assert.Equal(8, alerts.CmbHeartbeat.SelectedIndex);
        var card = Assert.Single(model.Channels);
        Assert.Contains("Heartbeat daily 09:30 + ping", card.Tags);
    });

    [Fact]
    public void ADeepLink_OpensThatConnectionsTab() => UiThread.Run(() =>
    {
        var page = Open(SettingsLocation.Connection(_down.Id, ConnectionTab.Thresholds));

        var editor = Editor(page);
        var view   = EditorView(page);
        Assert.Equal(_down.Id, editor.ConnectionId);
        Assert.Equal(ConnectionTab.Thresholds, editor.SelectedTab);
        Assert.Same(view.TabThresholds, view.Tabs.SelectedItem);
        Assert.Equal("Down", view.TxtName.Text);
        Assert.Equal("UNREACHABLE", view.TxtStatusChip.Text);

        // Another tab of the same connection: the page stays, only the tab changes.
        UiThread.Wait(page.OpenAsync(SettingsLocation.Connection(_down.Id, ConnectionTab.Advanced)));
        Assert.Same(editor, Editor(page));
        Assert.Equal(ConnectionTab.Advanced, editor.SelectedTab);
        Assert.Same(view.TabAdvanced, view.Tabs.SelectedItem);
    });

    [Fact]
    public void ALinkToARemovedConnection_ShowsTheCards() => UiThread.Run(() =>
    {
        var page = Open(SettingsLocation.Connection(Guid.NewGuid()));

        Assert.Null(page.ViewModel.Connections!.Editor);
        Assert.Equal(3, page.ViewModel.Connections.Cards.Count);
    });

    // ── Connections ───────────────────────────────────────────────────────────

    [Fact]
    public void Cards_ShowEveryConnection_InTheSelectorsOrder_WithItsRoleTagsAndState() => UiThread.Run(() =>
    {
        var page  = Open();
        var cards = page.ViewModel.Connections!.Cards.Select(c => c.Card).ToList();

        Assert.Equal(["Down", "Hot", "Quiet"], cards.Select(c => c.Name));
        Assert.Equal([ConnectionRole.Background, ConnectionRole.Active, ConnectionRole.Off], cards.Select(c => c.Role));
        Assert.Equal(["Unreachable · can't connect", "Critical: CPU at 99 % · score 84", "No score: not monitored"], cards.Select(c => c.StateText));
        Assert.Equal(["Windows auth", "Monitor in background", "Default thresholds"], cards[0].Tags);
        Assert.Equal(["Windows auth", "Monitored", "Default thresholds"], cards[1].Tags);
        Assert.Equal(["Windows auth", "Not monitored", "Default thresholds"], cards[2].Tags);

        // The cards, then "+ Add a connection", which opens a new connection's page.
        var view = page.Connections!;
        Assert.Equal(4, view.Cards.Items.Count);
        var add = UiThread.Find<Button>(view.Cards).Single(b => Equals(b.Content, "+ Add a connection"));
        TemplateRolloutTests.Click(add);
        Assert.True(page.ViewModel.Connections.Editor!.IsNew);
    });

    [Fact]
    public void NewConnection_SaysWhatsMissing_AndCantBeSavedUntilItsThere() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.NewConnectionPage);
        var editor = Editor(page);
        var view   = EditorView(page);

        Assert.True(editor.IsNew);
        Assert.Equal("New connection", view.TxtName.Text);
        Assert.False(editor.IsDirty);
        Assert.Equal(Visibility.Visible, view.BtnSaveAndConnect.Visibility);

        // Nothing typed yet: Save is disabled and the bar says why, with no field marked yet.
        Assert.False(Validation.GetHasError(view.TxtServer));
        Assert.False(view.BtnSave.IsEnabled);
        Assert.Equal("Server name is required.", view.TxtProblem.Text);
        Assert.Null(editor.ServerError);
        Assert.False(Await(editor.SaveAsync(connect: false)));
        Assert.Equal("✗  Server name is required.", view.TxtStatus.Text);

        // Typed and cleared again: the field says it too, in red.
        view.TxtServer.Text = "sql01";
        view.TxtServer.Text = "";
        UiThread.Layout(page, 1200, 900);
        Assert.Equal("Server name is required.", editor.ServerError);
        Assert.Equal(((System.Windows.Media.SolidColorBrush)page.FindResource("Danger")).Color,
                     ((System.Windows.Media.SolidColorBrush)view.TxtServer.BorderBrush).Color);
        Assert.Equal(((System.Windows.Media.SolidColorBrush)page.FindResource("Border")).Color,
                     ((System.Windows.Media.SolidColorBrush)view.TxtUserName.BorderBrush).Color);
    });

    // ── A connection's page ───────────────────────────────────────────────────

    [Fact]
    public void Save_WritesEveryTab_InOneWrite_WithoutSigningIn() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_quiet.Id));
        var editor = Editor(page);

        editor.Name = "Quiet reporting";
        editor.Notifies = false;
        editor.OwnThresholds = true;
        editor.Thresholds.Row(HealthIndicator.Cpu).Warning.Text = "60";

        Assert.True(editor.IsDirty);
        Assert.Equal("●  Unsaved changes on Connection, Monitoring & alerts and Health thresholds", editor.UnsavedText);
        Assert.True(editor.ShowsOwnTag);

        var saved  = false;
        var writes = CountChanges(() => saved = Await(editor.SaveAsync(connect: false)));

        Assert.True(saved, editor.Status.Text);
        Assert.Equal(1, writes);
        Assert.False(editor.IsDirty);
        Assert.Equal(string.Empty, editor.UnsavedText);

        var store = _settings!.Load();
        var quiet = store.Connections.Single(c => c.Id == _quiet.Id);
        Assert.Equal("Quiet reporting", quiet.Name);
        Assert.False(store.NotifiesFor(_quiet.Id));
        Assert.Equal(60, quiet.HealthThresholds!.CpuPct!.Warning);
        Assert.Equal(_hot.Id, store.ActiveConnectionId);
        Assert.Equal([false], _applied);
    });

    [Fact]
    public void SaveAndMakeActive_LetsTheAppLeaveSettings_ForLiveMetrics() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_quiet.Id));
        var editor = Editor(page);
        editor.Name = "Quiet 2";
        bool? couldLeave = null;
        _onApply = async () => couldLeave = await page.ViewModel.TryLeaveAsync();

        Assert.True(Await(editor.SaveAsync(connect: true)), editor.Status.Text);

        Assert.Equal([true], _applied);
        Assert.True(couldLeave);
        Assert.Empty(Asked);
        Assert.Equal(_quiet.Id, _settings!.Load().ActiveConnectionId);
    });

    [Fact]
    public void Save_KeepsAMuteMadeElsewhere_WhileThePageWasOpen() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_quiet.Id));
        var editor = Editor(page);

        // From the tray menu, say, after the page was opened.
        _settings!.SetNotificationsMuted(_quiet.Id, muted: true);
        editor.Name = "Quiet 2";
        Assert.True(Await(editor.SaveAsync(connect: false)), editor.Status.Text);

        Assert.False(_settings.Load().NotifiesFor(_quiet.Id));
    });

    [Fact]
    public void UnsavedChanges_StayWhenMovingBetweenTabs_AndDiscardPutsBackWhatsSaved() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_quiet.Id));
        var editor = Editor(page);
        var view   = EditorView(page);

        // Typed in the boxes, on two tabs.
        view.TxtConnectionName.Text = "Renamed";
        editor.SelectTab(ConnectionTab.Advanced);
        UiThread.Layout(page, 1200, 900);
        view.TxtConnectTimeout.Text = "45";
        editor.SelectTab(ConnectionTab.Connection);
        UiThread.Layout(page, 1200, 900);

        Assert.Equal("Renamed", view.TxtConnectionName.Text);
        Assert.Equal("45", editor.ConnectTimeout);
        Assert.Equal("●  Unsaved changes on Connection and Advanced", view.TxtUnsaved.Text);
        Assert.True(view.BtnDiscard.IsEnabled);

        TemplateRolloutTests.Click(view.BtnDiscard);

        Assert.False(editor.IsDirty);
        Assert.Equal("Quiet", view.TxtConnectionName.Text);
        Assert.Equal("30", editor.ConnectTimeout);
        Assert.False(view.BtnDiscard.IsEnabled);
        Assert.False(view.BtnSave.IsEnabled);
    });

    [Fact]
    public void ThePasswordBox_IsCopiedToTheViewModel_AndBackOnDiscard() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_quiet.Id));
        var editor = Editor(page);
        var view   = EditorView(page);
        editor.Authentication = SqlAuthMode.SqlServer;
        editor.UserName = "app";
        UiThread.Layout(page, 1200, 900);
        Assert.Equal(Visibility.Visible, view.PanelPassword.Visibility);

        view.TxtPassword.Password = "s3cret";
        Assert.Equal("s3cret", editor.Password);
        Assert.Null(editor.PasswordError);

        TemplateRolloutTests.Click(view.BtnDiscard);
        Assert.Equal(string.Empty, view.TxtPassword.Password);
        Assert.Equal(Visibility.Collapsed, view.PanelPassword.Visibility);
    });

    [Fact]
    public void LeavingWithUnsavedChanges_Asks_CancelStays_AndNoDiscards() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_quiet.Id));
        var editor = Editor(page);
        editor.Name = "Renamed";

        _dialogs.SaveAnswer = DialogAnswer.Cancel;
        Assert.False(Await(page.ViewModel.TryLeaveAsync()));

        // A click on another category that the user takes back: the menu shows Connections again.
        TemplateRolloutTests.Click(MenuButton(page, "Alerts"));
        UiThread.Layout(page, 1200, 900);

        Assert.Equal(SettingsCategory.Connections, page.Category);
        Assert.Same(editor, Editor(page));
        Assert.True(MenuButton(page, "Connections").IsChecked);
        Assert.False(MenuButton(page, "Alerts").IsChecked);
        Assert.Equal(2, Asked.Count);
        Assert.All(Asked, a => Assert.StartsWith("AskToSave: Unsaved changes: Save the changes to \"Quiet\"?", a, StringComparison.Ordinal));

        _dialogs.SaveAnswer = DialogAnswer.No;
        UiThread.Wait(page.OpenAsync(new SettingsLocation(SettingsCategory.Alerts)));

        Assert.Equal(SettingsCategory.Alerts, page.Category);
        Assert.Equal("Quiet", _settings!.Load().Connections.Single(c => c.Id == _quiet.Id).Name);
    });

    [Fact]
    public void LeavingWithUnsavedChanges_AndChoosingSave_SavesThemFirst() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_quiet.Id, ConnectionTab.Monitoring));
        var view   = EditorView(page);
        view.ChkMonitorInBackground.IsChecked = true;

        _dialogs.SaveAnswer = DialogAnswer.Yes;
        UiThread.Wait(page.OpenAsync(new SettingsLocation(SettingsCategory.Monitoring)));

        Assert.Equal(SettingsCategory.Monitoring, page.Category);
        Assert.True(_settings!.Load().Connections.Single(c => c.Id == _quiet.Id).MonitorInBackground);
        Assert.True(_monitoring!.IsMonitoring(_quiet.Id));
    });

    [Fact]
    public void TheBreadcrumb_GoesBackToTheCards_AfterAsking() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_quiet.Id));
        var editor = Editor(page);
        editor.Name = "Renamed";
        _dialogs.SaveAnswer = DialogAnswer.No;

        Assert.Same(editor.BackCommand, EditorView(page).LnkBack.Command);
        UiThread.Wait(editor.BackCommand.ExecuteAsync(null));
        UiThread.Layout(page, 1200, 900);

        Assert.Null(page.ViewModel.Connections!.Editor);
        Assert.Null(page.Connections!.Editor);
        Assert.Equal(Visibility.Visible, page.Connections.ListView.Visibility);
        Assert.Single(Asked);
    });

    [Fact]
    public void Remove_AsksFirst_ThenShowsTheCardsWithoutIt() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_quiet.Id));
        var editor = Editor(page);

        _dialogs.ConfirmAnswer = false;
        Assert.False(Await(editor.RemoveAsync()));
        Assert.Equal(3, _settings!.Load().Connections.Count);

        _dialogs.ConfirmAnswer = true;
        Assert.True(Await(editor.RemoveAsync()));

        var connections = page.ViewModel.Connections!;
        Assert.Equal(2, _settings.Load().Connections.Count);
        Assert.Null(connections.Editor);
        Assert.Equal(["Down", "Hot"], connections.Cards.Select(c => c.Card.Name));
        Assert.Equal("✓  Removed \"Quiet\".", page.Connections!.TxtStatus.Text);
        Assert.StartsWith("Confirm: Remove Connection: Remove the connection \"Quiet\"", Asked[^1], StringComparison.Ordinal);
    });

    // ── Health thresholds ─────────────────────────────────────────────────────

    [Fact]
    public void TheThresholdsTab_PreviewsTheLatestSample_OnWhatsTyped() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_hot.Id, ConnectionTab.Thresholds));
        var editor = Editor(page);
        var view   = EditorView(page);
        UiThread.Layout(page, 1200, 900);
        var cpu = editor.Thresholds.Row(HealthIndicator.Cpu);

        // The defaults, read-only, with the sample graded on them: CPU 99 % is critical.
        Assert.True(editor.Thresholds.IsReadOnly);
        Assert.True(view.ConnectionThresholds.BoxOf(cpu.Warning).IsReadOnly);
        Assert.Equal(("99 %", "−16"), (cpu.NowText, cpu.PointsText));
        Assert.Equal(Visibility.Collapsed, view.TxtPreview.Visibility);

        view.ChkCustomThresholds.IsChecked = true;
        view.ConnectionThresholds.BoxOf(cpu.Critical).Text = "100";
        view.ConnectionThresholds.BoxOf(cpu.Warning).Text  = "99.5";
        UiThread.Layout(page, 1200, 900);

        Assert.False(view.ConnectionThresholds.BoxOf(cpu.Warning).IsReadOnly);
        Assert.Equal(Visibility.Visible, view.TxtPreview.Visibility);
        Assert.Equal("On the latest sample, Hot would score 84 → 100", view.TxtPreview.Text);
        Assert.Equal(("99 %", "0"), (cpu.NowText, cpu.PointsText));

        // Turning it off again shows the defaults: nothing left to save.
        editor.OwnThresholds = false;
        Assert.False(editor.IsDirty);
        Assert.Equal("75", view.ConnectionThresholds.BoxOf(cpu.Warning).Text);
    });

    [Fact]
    public void WithNoSample_ThereIsNoPreview() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_quiet.Id, ConnectionTab.Thresholds));
        var editor = Editor(page);

        editor.OwnThresholds = true;
        editor.Thresholds.Row(HealthIndicator.Cpu).Warning.Text = "50";

        Assert.Null(editor.Preview);
        Assert.Equal(("—", "—"), (editor.Thresholds.Row(HealthIndicator.Cpu).NowText, editor.Thresholds.Row(HealthIndicator.Cpu).PointsText));
    });

    [Fact]
    public void ABoxThatCantBeRead_OnItsOwnThresholds_DisablesSave_AndSaysWhy() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_quiet.Id, ConnectionTab.Thresholds));
        var editor = Editor(page);
        var view   = EditorView(page);

        editor.OwnThresholds = true;
        editor.Thresholds.Row(HealthIndicator.Cpu).Warning.Text = "abc";
        UiThread.Layout(page, 1200, 900);

        Assert.False(view.BtnSave.IsEnabled);
        // Said under the table, not as WPF's red frame around the switch.
        Assert.False(Validation.GetHasError(view.ChkCustomThresholds));
        Assert.StartsWith("Health thresholds — CPU", editor.ThresholdsError);
        Assert.Equal(Visibility.Visible, view.TxtThresholdsError.Visibility);
        Assert.Equal(editor.ThresholdsError, view.TxtProblem.Text);
    });

    [Fact]
    public void TheDefaultThresholds_SaveOnlyWithSave_AndNotWhenHalfTyped() => UiThread.Run(() =>
    {
        var page = Open(new SettingsLocation(SettingsCategory.Thresholds));
        var category = Category<ThresholdsCategoryViewModel>(page);
        var view = Assert.IsType<ThresholdsCategory>(page.Shown);
        var cpu = category.Thresholds.Row(HealthIndicator.Cpu).Warning;

        view.GlobalThresholds.BoxOf(cpu).Text = "abc";
        Assert.True(category.IsDirty);
        Assert.False(view.BtnSave.IsEnabled);
        Assert.StartsWith("CPU", view.TxtProblem.Text);
        Assert.Equal(75, _settings!.Load().HealthThresholds.CpuPct!.Warning);

        Assert.False(category.Save());
        Assert.Equal(75, _settings.Load().HealthThresholds.CpuPct!.Warning);

        view.GlobalThresholds.BoxOf(cpu).Text = "70";
        Assert.True(view.BtnSave.IsEnabled);
        TemplateRolloutTests.Click(view.BtnSave);
        Assert.Equal(70, _settings.Load().HealthThresholds.CpuPct!.Warning);
        Assert.False(category.IsDirty);
        Assert.Equal(70, _monitoring!.Get(_hot.Id)!.Thresholds.CpuPct!.Warning);
    });

    [Fact]
    public void TheDefaultThresholds_PreviewTheWeights_SaveThem_AndLogThem() => UiThread.Run(() =>
    {
        var page = Open(new SettingsLocation(SettingsCategory.Thresholds));
        var category = Category<ThresholdsCategoryViewModel>(page);
        var logged = new List<ActivityEntry>();
        _settings!.Changed += logged.AddRange;

        // CPU at 99 % costs Hot 16 points; weighted 0, it costs none (#173).
        Assert.Equal("On the latest sample, Hot would still score 84", category.Preview);
        category.Thresholds.Row(HealthIndicator.Cpu).Weight.Text = "0";
        Assert.True(category.IsDirty);
        Assert.Equal("On the latest sample, Hot would score 84 → 100", category.Preview);

        Assert.True(category.Save());
        Assert.Equal(0, _settings.Load().HealthThresholds.Weight(HealthIndicator.Cpu));
        Assert.Equal(0, _monitoring!.Get(_hot.Id)!.Thresholds.Weight(HealthIndicator.Cpu));
        Assert.Equal(100, _monitoring.Get(_hot.Id)!.Score!.Value);
        var row = Assert.Single(logged, r => r.Setting == "Health weight: CPU");
        Assert.Equal(("16", "0"), (row.OldValue, row.NewValue));
        Assert.Equal(SettingPlace.In(SettingsCategory.Thresholds), SettingsNav.PlaceOf(row.Setting!));
    });

    [Fact]
    public void TheDefaultThresholds_PleScaling_IsSaved_AndLogged() => UiThread.Run(() =>
    {
        var page = Open(new SettingsLocation(SettingsCategory.Thresholds));
        var category = Category<ThresholdsCategoryViewModel>(page);
        var logged = new List<ActivityEntry>();
        _settings!.Changed += logged.AddRange;
        Assert.True(category.Thresholds.PleScaled);

        category.Thresholds.PleScaled = false;
        Assert.True(category.IsDirty);
        Assert.True(category.Save());

        Assert.False(_settings.Load().HealthThresholds.ScalesPageLifeExpectancy);
        var row = Assert.Single(logged, r => r.Setting == "Health threshold: Page life expectancy scaling");
        Assert.Equal(("Per 4 GB of buffer pool", "Fixed"), (row.OldValue, row.NewValue));
    });

    [Fact]
    public void TheDiskGbThresholds_AreSaved_Checked_AndLogged() => UiThread.Run(() =>
    {
        var page = Open(new SettingsLocation(SettingsCategory.Thresholds));
        var category = Category<ThresholdsCategoryViewModel>(page);
        var table = category.Thresholds;
        var logged = new List<ActivityEntry>();
        _settings!.Changed += logged.AddRange;
        Assert.Equal(("50", "20"), (table.GbWarning.Text, table.GbCritical.Text));

        // The critical one must be below the warning one, as for any check where lower is worse.
        table.GbCritical.Text = "80";
        Assert.Equal("Disk free (GB): the critical threshold must be lower than the warning one.", category.Problem);
        Assert.False(category.Save());
        Assert.Equal("Disk free (GB): the critical threshold must be lower than the warning one.",
                     category.Status.Text.TrimStart('✗', ' ', '⚠'));

        table.GbWarning.Text = "100";
        table.GbCritical.Text = "";
        Assert.True(category.Save());

        Assert.Equal(new HealthThreshold(100, null), _settings.Load().HealthThresholds.DiskFreeGb);
        Assert.Equal(new HealthThreshold(100, null), _monitoring!.Get(_hot.Id)!.Thresholds.DiskFreeGb);
        var row = Assert.Single(logged, r => r.Setting == "Health threshold: Disk free (GB)");
        Assert.Equal(("warning 50 GB, critical 20 GB", "warning 100 GB, critical off"), (row.OldValue, row.NewValue));
    });

    [Fact]
    public void Weights_AllZero_OrOutOfRange_AreNotSaved() => UiThread.Run(() =>
    {
        var page = Open(new SettingsLocation(SettingsCategory.Thresholds));
        var category = Category<ThresholdsCategoryViewModel>(page);
        var table = category.Thresholds;

        table.Row(HealthIndicator.Cpu).Weight.Text = "101";
        Assert.False(category.Save());
        Assert.Equal("CPU: enter a weight from 0 to 100; 0 leaves it out of the score.", category.Status.Text.TrimStart('✗', ' ', '⚠'));

        foreach (var indicator in Enum.GetValues<HealthIndicator>())
            table.Row(indicator).Weight.Text = "0";
        Assert.False(category.Save());
        Assert.Equal(HealthWeights.Default, _settings!.Load().HealthThresholds.Weights);
    });

    [Fact]
    public void LeavingTheDefaultThresholds_HalfTyped_Asks() => UiThread.Run(() =>
    {
        var page = Open(new SettingsLocation(SettingsCategory.Thresholds));
        var category = Category<ThresholdsCategoryViewModel>(page);
        category.Thresholds.Row(HealthIndicator.Cpu).Warning.Text = "70";

        _dialogs.SaveAnswer = DialogAnswer.Cancel;
        UiThread.Wait(page.OpenAsync(new SettingsLocation(SettingsCategory.Alerts)));

        Assert.Equal(SettingsCategory.Thresholds, page.Category);
        Assert.Single(Asked);
    });

    // ── Categories for the whole app ──────────────────────────────────────────

    [Fact]
    public void AlertsAndChat_ListsTheMutedConnections_AndItsSwitchMutesThemAll() => UiThread.Run(() =>
    {
        var page = Open(new SettingsLocation(SettingsCategory.Alerts));
        var alerts = Category<AlertsCategoryViewModel>(page);
        var view = Assert.IsType<AlertsCategory>(page.Shown);
        Assert.True(view.ChkNotifications.IsChecked);
        Assert.Equal(Visibility.Collapsed, view.MutedLine.Visibility);

        _settings!.SetNotificationsMuted(_quiet.Id, muted: true);
        UiThread.Wait(page.OpenAsync(new SettingsLocation(SettingsCategory.Appearance)));
        UiThread.Wait(page.OpenAsync(new SettingsLocation(SettingsCategory.Alerts)));
        UiThread.Layout(page, 1200, 900);

        Assert.True(view.ChkNotifications.IsChecked);
        Assert.Equal("Muted: ", view.TxtMuted.Text);
        var muted = Assert.Single(alerts.Muted);
        Assert.Equal(("Quiet", ""), (muted.Name, muted.Separator));

        // The link opens its Monitoring & alerts tab.
        muted.Open.Execute(null);
        UiThread.WaitUntil(() => page.Category == SettingsCategory.Connections);
        Assert.Equal((_quiet.Id, ConnectionTab.Monitoring), (Editor(page).ConnectionId, Editor(page).SelectedTab));

        UiThread.Wait(page.OpenAsync(new SettingsLocation(SettingsCategory.Alerts)));
        UiThread.Layout(page, 1200, 900);
        TemplateRolloutTests.Click(view.ChkNotifications);

        var store = _settings.Load();
        Assert.All(store.Connections, c => Assert.False(store.NotifiesFor(c.Id)));
        Assert.False(view.ChkNotifications.IsChecked);
        Assert.Equal("Every connection is muted: ", alerts.MutedText);
    });

    [Fact]
    public void CommandTimeout_SavesOnItsOwn_AndIsApplied() => UiThread.Run(() =>
    {
        var page = Open(new SettingsLocation(SettingsCategory.Monitoring));
        var monitoring = Category<MonitoringCategoryViewModel>(page);
        var view = Assert.IsType<MonitoringCategory>(page.Shown);
        Assert.Equal("30", view.TxtTimeout.Text);

        view.TxtTimeout.Text = "0";
        UiThread.Wait(monitoring.SaveTimeoutAsync());
        Assert.Equal(30, _settings!.Load().CommandTimeoutSeconds);
        Assert.StartsWith("✗", view.TxtMonitoringStatus.Text);

        view.TxtTimeout.Text = "90";
        UiThread.Wait(monitoring.SaveTimeoutCommand.ExecuteAsync(null));

        Assert.Equal(90, _settings.Load().CommandTimeoutSeconds);
        Assert.Equal([false], _applied);

        // Saving a connection keeps it.
        var editor = new ConnectionEditorViewModel(TestContext(), _quiet.Id) { Name = "Quiet 2" };
        Assert.True(Await(editor.SaveAsync(connect: false)));
        Assert.Equal(90, _settings.Load().CommandTimeoutSeconds);
    });

    [Fact]
    public void StartMonitoringAutomatically_TurnedOff_StopsEveryConnection() => UiThread.Run(() =>
    {
        var page = Open(new SettingsLocation(SettingsCategory.Monitoring));
        var view = Assert.IsType<MonitoringCategory>(page.Shown);
        Assert.True(view.ChkStartMonitoringAutomatically.IsChecked);

        TemplateRolloutTests.Click(view.ChkStartMonitoringAutomatically);
        UiThread.WaitUntil(() => _applied.Count == 1);

        Assert.False(_settings!.Load().StartMonitoringAutomatically);
        Assert.False(view.ChkStartMonitoringAutomatically.IsChecked);
        Assert.False(_monitoring!.IsMonitoring(_hot.Id));
        Assert.StartsWith("✓  Saved: monitoring stopped for every connection.", view.TxtMonitoringStatus.Text);
    });

    [Fact]
    public void QueryText_SavesAndApplies_AndClearAsksFirst_ThenClearsHereAndInTheService() => UiThread.Run(() =>
    {
        Directory.CreateDirectory(Path.Combine(_dir, "collector"));
        _control = new FakeCollectorControl(Path.Combine(_dir, "collector")) { State = CollectorServiceState.Running };
        var page = Open(new SettingsLocation(SettingsCategory.Monitoring));
        var monitoring = Category<MonitoringCategoryViewModel>(page);
        var view = Assert.IsType<MonitoringCategory>(page.Shown);
        Assert.Equal("Without literals", ((Choice<QueryTextStorage>)view.CmbQueryText.SelectedItem).Text);
        Assert.Same(monitoring.ClearQueryTextCommand, view.BtnClearQueryText.Command);

        view.CmbQueryText.SelectedValue = QueryTextStorage.Off;

        Assert.Equal(QueryTextStorage.Off, _settings!.Load().QueryTextStorage);
        Assert.Equal(QueryTextStorage.Off, _monitoring!.Get(_hot.Id)!.History!.QueryTexts);
        Assert.StartsWith("✓  Saved: no query text is kept from now on", view.TxtHistoryStatus.Text);

        // No: nothing is cleared, nothing asked of the service.
        _dialogs.ConfirmAnswer = false;
        UiThread.Wait(monitoring.ClearQueryTextCommand.ExecuteAsync(null));
        Assert.Contains(Asked, a => a.StartsWith("Confirm: Clear Stored Query Text: Delete the text of every query", StringComparison.Ordinal));
        Assert.Null(_control.Saved().QueryTextClearRequestedUtc);

        _dialogs.ConfirmAnswer = true;
        UiThread.Wait(monitoring.ClearQueryTextAsync());

        Assert.NotNull(_control.Saved().QueryTextClearRequestedUtc);
        Assert.StartsWith("✓  Query text cleared. The collector service clears its own", view.TxtClearQueryTextStatus.Text);
        Assert.Contains(ActivityEvents.QueryTextCleared,
                        new HistoryReader(Path.Combine(_dir, "history.db")).ReadActivity(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1))
                                                                           .Select(a => a.Entry.Event));
    });

    [Fact]
    public void ClearQueryText_DoesntAskAServiceThatIsntInstalled_ThoughItsFolderIsLeft() => UiThread.Run(() =>
    {
        Directory.CreateDirectory(Path.Combine(_dir, "collector"));
        _control = new FakeCollectorControl(Path.Combine(_dir, "collector"));   // not installed
        var page = Open(new SettingsLocation(SettingsCategory.Monitoring));
        var monitoring = Category<MonitoringCategoryViewModel>(page);
        _dialogs.ConfirmAnswer = true;

        UiThread.Wait(monitoring.ClearQueryTextAsync());

        Assert.Equal("✓  Query text cleared.", monitoring.ClearQueryTextStatus.Text);
        Assert.False(File.Exists(Engine.Service.CollectorPaths.SettingsFile(Path.Combine(_dir, "collector"))));
    });

    [Fact]
    public void ARetentionCutShort_AsksFirst_AndNoPutsTheSavedOneBack() => UiThread.Run(() =>
    {
        var page = Open(new SettingsLocation(SettingsCategory.Monitoring));
        var monitoring = Category<MonitoringCategoryViewModel>(page);
        var view = Assert.IsType<MonitoringCategory>(page.Shown);
        var saved = monitoring.RetentionDays;
        var shorter = monitoring.RetentionChoices.Select(c => c.Value).Where(d => d < saved).Max();

        _dialogs.ConfirmAnswer = false;
        view.CmbHistoryRetention.SelectedValue = shorter;
        UiThread.Layout(page, 1200, 900);

        Assert.StartsWith("Confirm: Monitoring History: Keep monitoring history for", Asked[^1], StringComparison.Ordinal);
        Assert.Equal(saved, _settings!.Load().HistoryRetentionDays);
        Assert.Equal(saved, view.CmbHistoryRetention.SelectedValue);
    });

    [Fact]
    public void Appearance_SwitchesTheTheme_AndTheDensity() => UiThread.Run(() =>
    {
        var page = Open(new SettingsLocation(SettingsCategory.Appearance));
        var view = Assert.IsType<AppearanceCategory>(page.Shown);
        var themeChanged = 0;
        page.ViewModel.ThemeChanged += () => themeChanged++;
        Assert.Equal("🌙  Dark", view.BtnThemeToggle.Content);

        TemplateRolloutTests.Click(view.BtnThemeToggle);
        Assert.Equal(1, themeChanged);

        view.RbCompact.IsChecked = true;
        Assert.Equal([GridDensity.Compact], _densities);
        Assert.Equal(GridDensity.Compact, _settings!.Load().GridDensity);
        Assert.False(view.RbComfortable.IsChecked);
        Assert.StartsWith("Compact:", view.TxtDensity.Text);
    });

    [Fact]
    public void TheNotificationArea_SavesAsItsClicked() => UiThread.Run(() =>
    {
        var page = Open(new SettingsLocation(SettingsCategory.NotificationArea));
        var view = Assert.IsType<NotificationAreaCategory>(page.Shown);
        var keep = _settings!.Load().KeepRunningInTray;

        TemplateRolloutTests.Click(view.ChkKeepRunningInTray);

        Assert.Equal(!keep, _settings.Load().KeepRunningInTray);
        Assert.Equal(!keep, view.ChkKeepRunningInTray.IsChecked);
        Assert.StartsWith("✓  Saved:", view.TxtTrayStatus.Text);
    });

    [Fact]
    public void AStatusLine_NeverShowsASecret()
    {
        var line = new StatusLine();

        line.Show("Could not post to https://hooks.slack.com/services/T1/B2/abc (Password=x;)", success: false);

        Assert.Equal("✗  Could not post to [webhook URL removed] (Password=*****;)", line.Text);
    }

    // ── Render: every category, each tab, both themes, no binding errors ─────

    public static TheoryData<string> Themes => new() { "DarkTheme", "LightTheme" };

    [Theory]
    [MemberData(nameof(Themes))]
    public void EveryCategory_Renders_WithoutABindingError(string theme) => UiThread.Run(() =>
    {
        using var themed = MergedPagesTests.Themed(theme);
        using var errors = BindingErrors.Watch();
        var page = Open();

        foreach (var info in SettingsNav.Categories)
        {
            UiThread.Wait(page.OpenAsync(new SettingsLocation(info.Id)));
            UiThread.Layout(page, 1200, 900);
            Render(page, theme, info.Id.ToString());
            Assert.True(page.Shown!.ActualHeight > 0, info.Title);
        }

        // An open chat channel form.
        UiThread.Wait(page.OpenAsync(new SettingsLocation(SettingsCategory.Alerts)));
        Category<AlertsCategoryViewModel>(page).AddChannelCommand.Execute(null);
        UiThread.Layout(page, 1200, 900);
        Render(page, theme, "Alerts-form");

        // A connection's page, each tab, and a new one.
        foreach (var tab in Enum.GetValues<ConnectionTab>())
        {
            UiThread.Wait(page.OpenAsync(SettingsLocation.Connection(_hot.Id, tab)));
            UiThread.Layout(page, 1200, 900);
            Render(page, theme, $"connection-{tab}");
        }
        Editor(page).OwnThresholds = true;
        Editor(page).Thresholds.Row(HealthIndicator.Cpu).Warning.Text = "abc";
        UiThread.Layout(page, 1200, 900);
        Render(page, theme, "connection-own-thresholds");
        _dialogs.SaveAnswer = DialogAnswer.No;
        UiThread.Wait(page.OpenAsync(SettingsLocation.NewConnectionPage));
        UiThread.Layout(page, 1200, 900);
        Render(page, theme, "connection-new");

        errors.AssertNone();
    });

    // To look at: %TEMP%\SqlVitalsTests\settings\<name>-<theme>.png.
    private static void Render(FrameworkElement element, string theme, string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", "settings");
        Directory.CreateDirectory(dir);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)element.ActualWidth, (int)element.ActualHeight, 96, 96,
                                                                          System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(dir, $"{name}-{theme}.png"));
        encoder.Save(file);
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
        _chat?.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
