using System.Collections.Concurrent;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Pages.Settings;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Settings;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Settings (#131) as WPF builds it, off screen, over the three connections of the Home tests:
/// "Hot" (active, CPU at 99 %, so critical), "Down" (monitored in the background, unreachable)
/// and "Quiet" (not monitored). No server is ever contacted: nothing here changes how a
/// connection connects, so saving doesn't sign in first.
/// </summary>
public sealed class SettingsPageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly ConcurrentQueue<(string Server, string Method)> _calls = new();
    private readonly List<bool> _applied = new();
    private readonly List<(string Text, MessageBoxButton Buttons)> _asked = new();

    private readonly ConnectionSettings _hot   = Conn("hot",   "Hot");
    private readonly ConnectionSettings _down  = Conn("down",  "Down", background: true);
    private readonly ConnectionSettings _quiet = Conn("quiet", "Quiet");

    private MonitoringManager?         _monitoring;
    private ConnectionSettingsService? _settings;
    private ChatNotificationService?   _chat;

    // Run as MainWindow applies a save, e.g. to try leaving Settings as opening Live Metrics does.
    private Func<Task>? _onApply;

    // What the next question is answered with.
    private MessageBoxResult _answer = MessageBoxResult.Cancel;

    // The collector service (#172): installed when a test sets it.
    private Fakes.FakeCollectorControl? _control;
    private CollectorLink?               _collectors;

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
        try { UiThread.Wait(_monitoring.Get(_down.Id)!.CollectNowAsync()); }
        catch (InvalidOperationException) { /* "No server in tests.": Down can't be reached */ }

        _chat = new ChatNotificationService(new NoSender(), "DBA-PC01", _ => { });
        _chat.Apply(_settings.Load());

        if (_control is not null)
        {
            _collectors = new CollectorLink(_control, _settings, new Fakes.FakeUiDispatcher(), TimeProvider.System);
            UiThread.Wait(_collectors.RefreshAsync());
        }
        var page = new SettingsPage(_settings, _monitoring, _chat, Apply, location, collectors: _collectors)
        {
            Prompt = (text, _, buttons, _) =>
            {
                _asked.Add((text, buttons));
                return _answer;
            },
        };
        UiThread.Layout(page, 1200, 900);
        return page;
    }

    // What MainWindow does after a save: monitoring follows the settings.
    private Task Apply(bool connect)
    {
        _applied.Add(connect);
        _monitoring!.Sync(_settings!.Load(), ActivitySources.Settings);
        return _onApply?.Invoke() ?? Task.CompletedTask;
    }

    private static void Click(ButtonBase button) => button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    // Lets the dispatcher run until the task is done, then its result.
    private static T Await<T>(Task<T> task)
    {
        UiThread.Wait(task);
        return task.Result;
    }

    private static ConnectionEditor Editor(SettingsPage page) => page.Connections!.Editor!;

    // The text of some inlines, links included.
    private static string Text(IEnumerable<System.Windows.Documents.Inline> inlines) => string.Concat(inlines.Select(i => i switch
    {
        System.Windows.Documents.Run run       => run.Text,
        System.Windows.Documents.Span span     => Text(span.Inlines),
        _                                      => string.Empty,
    }));

    private int CountChanges(Action action)
    {
        var writes = 0;
        void Count(IReadOnlyList<ActivityEntry> _) => writes++;
        _settings!.Changed += Count;
        try { action(); }
        finally { _settings.Changed -= Count; }
        return writes;
    }

    // ── The side menu ─────────────────────────────────────────────────────────

    [Fact]
    public void OpensOnConnections_AndTheSideMenuSwitchesCategory_BuildingEachWhenFirstOpened() => UiThread.Run(() =>
    {
        var page = Open();

        Assert.Equal(SettingsCategory.Connections, page.Category);
        Assert.IsType<ConnectionsCategory>(page.Shown);
        Assert.True(page.MenuItemFor(SettingsCategory.Connections).IsChecked);
        Assert.Equal("Connections, 3 saved", System.Windows.Automation.AutomationProperties.GetName(page.MenuItemFor(SettingsCategory.Connections)));

        var shown = new List<SettingsCategory>();
        page.CategoryShown += shown.Add;
        var alerts = page.MenuItemFor(SettingsCategory.Alerts);
        alerts.IsChecked = true;
        Click(alerts);

        Assert.Equal(SettingsCategory.Alerts, page.Category);
        Assert.IsType<AlertsCategory>(page.Shown);
        Assert.False(page.MenuItemFor(SettingsCategory.Connections).IsChecked);
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
        var collector = Assert.IsType<CollectorCategory>(page.Shown);

        Assert.StartsWith("Not installed. SqlVitals watches your servers itself while it runs.", collector.TxtState.Text);
        Assert.False(collector.BtnStart.IsEnabled);
        Assert.Equal(["Down", "Hot", "Quiet"], collector.Rows.Select(r => r.Name));
        Assert.All(collector.Rows, r => Assert.False(r.CanAct));
        Assert.All(collector.Rows, r => Assert.Equal("The collector service isn't installed on this PC.", r.ActionToolTip));
        Assert.Equal(0, collector.CmbChoice.SelectedIndex);
    });

    [Fact]
    public void TheCollectorService_HandsAServerOver_AndTakesItBack() => UiThread.Run(() =>
    {
        Directory.CreateDirectory(Path.Combine(_dir, "collector"));
        _control = new Fakes.FakeCollectorControl(Path.Combine(_dir, "collector")) { State = CollectorServiceState.Running };
        var page = Open(new SettingsLocation(SettingsCategory.Collector));
        var collector = Assert.IsType<CollectorCategory>(page.Shown);
        Assert.StartsWith("Running. It watches 0 of your servers", collector.TxtState.Text);
        Assert.False(collector.BtnStart.IsEnabled);
        Assert.True(collector.BtnStop.IsEnabled);

        var hot = collector.Rows.Single(r => r.Name == "Hot");
        Assert.Equal(("Hand over", true, "Watched in SqlVitals."), (hot.ActionText, hot.CanAct, hot.Where));
        hot.Act.Execute(null);
        UiThread.WaitUntil(() => collector.Rows.Single(r => r.Name == "Hot").ActionText == "Take back");

        Assert.Equal(_hot.Id, Assert.Single(_control.Saved().Servers).Id);
        Assert.StartsWith("✓  Handed Hot to the service.", collector.TxtServersStatus.Text);
        Assert.StartsWith("Running. It watches 1 of your servers", collector.TxtState.Text);

        collector.Rows.Single(r => r.Name == "Hot").Act.Execute(null);
        UiThread.WaitUntil(() => collector.Rows.Single(r => r.Name == "Hot").ActionText == "Hand over");
        Assert.Empty(_control.Saved().Servers);

        // What Start does is saved as it's picked, and logged as a setting.
        collector.CmbChoice.SelectedIndex = (int)WatchChoice.Service;
        Assert.Equal(WatchChoice.Service, _settings!.Load().WatchChoice);
    });

    [Fact]
    public void TheChatChannels_SayPlainly_ThatAlertsStopWhenThePcSleepsOrTheAppCloses() => UiThread.Run(() =>
    {
        var page = Open(new SettingsLocation(SettingsCategory.Alerts));
        var alerts = Assert.IsType<AlertsCategory>(page.Shown);

        // On the screen itself (#170), not only in the wiki, and whether or not there are channels.
        Assert.Equal(System.Windows.Visibility.Visible, alerts.ChatStopsWarning.Visibility);
        Assert.StartsWith("⚠  Alerts stop when this PC sleeps, shuts down, or SqlVitals is closed", alerts.TxtChatStopsWarning.Text);
    });

    [Fact]
    public void AChannelsHeartbeat_IsChosenInItsForm_WithItsPingUrlMaskedOnceSaved() => UiThread.Run(() =>
    {
        const string ping = "https://hc-ping.com/0f3b8a5e-1c2d-4e5f-8a9b-0c1d2e3f4a5b";
        var page = Open(new SettingsLocation(SettingsCategory.Alerts));
        var alerts = Assert.IsType<AlertsCategory>(page.Shown);
        Click(alerts.BtnAddChatChannel);
        alerts.TxtChatName.Text = "DBA team";
        alerts.TxtChatUrl.Text  = "https://prod-27.westeurope.logic.azure.com:443/workflows/0a1b/triggers/manual/paths/invoke?sig=AbCd";

        // Daily at shows the time; the steps follow the service and the schedule.
        alerts.CmbHeartbeat.SelectedIndex = 8;
        Assert.Equal(System.Windows.Visibility.Visible, alerts.CmbHeartbeatAt.Visibility);
        alerts.CmbHeartbeatAt.SelectedIndex = 19; // 09:30
        Assert.Equal(HeartbeatSchedule.DailyAt(new TimeSpan(9, 30, 0)), alerts.ChosenHeartbeat());
        alerts.ChkHeartbeatSteps.IsChecked = true;
        Assert.Equal(System.Windows.Visibility.Visible, alerts.PnlHeartbeatSteps.Visibility);
        Assert.Contains("Recurrence: every day, starting 10:00", alerts.TxtHeartbeatSteps.Text);

        // A ping URL that isn't https blocks the save, and says why.
        alerts.TxtPingUrl.Text = "http://hc-ping.com/abc";
        Click(alerts.BtnSaveChatChannel);
        Assert.Contains("must start with https://", alerts.TxtChatFormMessage.Text);
        Assert.Empty(_settings!.Load().ChatChannels);

        alerts.TxtPingUrl.Text = ping;
        Click(alerts.BtnSaveChatChannel);

        var saved = Assert.Single(_settings.Load().ChatChannels);
        Assert.Equal((HeartbeatEvery.Daily, new TimeSpan(9, 30, 0), ping), (saved.HeartbeatEvery, saved.HeartbeatAt, saved.HeartbeatPingUrl));
        // Reopened after saving: the ping URL is in no control until Show.
        Assert.Equal(string.Empty, alerts.TxtPingUrl.Text);
        Assert.StartsWith("hc-ping.com/…/4a5b", alerts.TxtPingUrlMasked.Text);
        // The webhook's Show shows both secrets.
        Click(alerts.BtnShowChatUrl);
        Assert.Equal(ping, alerts.TxtPingUrl.Text);
        // Copy steps is a bound command, not a Click= (the real clipboard is left alone here).
        Assert.Same(alerts.CopyHeartbeatSteps, alerts.BtnCopyHeartbeatSteps.Command);
        Assert.Equal(8, alerts.CmbHeartbeat.SelectedIndex);
        var card = Assert.Single((IEnumerable<ChatChannelCard>)alerts.ChannelCards.ItemsSource);
        Assert.Contains("Heartbeat daily 09:30 + ping", card.Tags);
    });

    [Fact]
    public void ADeepLink_OpensThatConnectionsTab() => UiThread.Run(() =>
    {
        var page = Open(SettingsLocation.Connection(_down.Id, ConnectionTab.Thresholds));

        var editor = Editor(page);
        Assert.Equal(_down.Id, editor.ConnectionId);
        Assert.Equal(ConnectionTab.Thresholds, editor.SelectedTab);
        Assert.Same(editor.TabThresholds, editor.Tabs.SelectedItem);
        Assert.Equal("Down", editor.TxtName.Text);
        Assert.Equal("UNREACHABLE", editor.TxtStatusChip.Text);

        // Another tab of the same connection: the page stays, only the tab changes.
        UiThread.Wait(page.OpenAsync(SettingsLocation.Connection(_down.Id, ConnectionTab.Advanced)));
        Assert.Same(editor, Editor(page));
        Assert.Equal(ConnectionTab.Advanced, editor.SelectedTab);
    });

    [Fact]
    public void ALinkToARemovedConnection_ShowsTheCards() => UiThread.Run(() =>
    {
        var page = Open(SettingsLocation.Connection(Guid.NewGuid()));

        Assert.Null(page.Connections!.Editor);
        Assert.Equal(3, page.Connections.CardsShown.Count);
    });

    // ── Connections ───────────────────────────────────────────────────────────

    [Fact]
    public void Cards_ShowEveryConnection_InTheSelectorsOrder_WithItsRoleTagsAndState() => UiThread.Run(() =>
    {
        var page  = Open();
        var cards = page.Connections!.CardsShown.Select(c => c.Card).ToList();

        Assert.Equal(["Down", "Hot", "Quiet"], cards.Select(c => c.Name));
        Assert.Equal([ConnectionRole.Background, ConnectionRole.Active, ConnectionRole.Off], cards.Select(c => c.Role));
        Assert.Equal(["Unreachable · can't connect", "Score 80 · Critical", "No score: not monitored"], cards.Select(c => c.StateText));
        Assert.Equal(["Windows auth", "Monitor in background", "Default thresholds"], cards[0].Tags);
        Assert.Equal(["Windows auth", "Monitored", "Default thresholds"], cards[1].Tags);
        Assert.Equal(["Windows auth", "Not monitored", "Default thresholds"], cards[2].Tags);

        // Two a row at this width, then "+ Add a connection".
        Assert.Equal(4, page.Connections.Cards.Items.Count);
        Assert.Equal("+ Add a connection", ((Button)page.Connections.Cards.Items[3]).Content);
    });

    [Fact]
    public void NewConnection_OpensAnEmptyPage_ThatSaysWhatsMissing() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.NewConnectionPage);
        var editor = Editor(page);

        Assert.True(editor.IsNew);
        Assert.Equal("New connection", editor.TxtName.Text);
        Assert.False(editor.IsDirty);
        Assert.True(editor.BtnSave.IsEnabled);
        Assert.Equal(Visibility.Visible, editor.BtnSaveAndConnect.Visibility);

        Assert.False(Await(editor.SaveAsync(connect: false)));
        Assert.Equal("✗  Server name is required.", editor.TxtStatus.Text);
    });

    // ── A connection's page ───────────────────────────────────────────────────

    [Fact]
    public void Save_WritesEveryTab_InOneWrite_WithoutSigningIn() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_quiet.Id));
        var editor = Editor(page);

        editor.TxtConnectionName.Text = "Quiet reporting";
        editor.ChkNotify.IsChecked = false;
        editor.ChkCustomThresholds.IsChecked = true;
        editor.ConnectionThresholds.Box(HealthIndicator.Cpu).Text = "60";

        Assert.True(editor.IsDirty);
        Assert.Equal("●  Unsaved changes on Connection, Monitoring & alerts and Health thresholds", editor.TxtUnsaved.Text);
        Assert.Equal(Visibility.Visible, editor.OwnTag.Visibility);

        var saved  = false;
        var writes = CountChanges(() => saved = Await(editor.SaveAsync(connect: false)));

        Assert.True(saved, editor.TxtStatus.Text);
        Assert.Equal(1, writes);
        Assert.False(editor.IsDirty);
        Assert.Equal(Visibility.Collapsed, editor.TxtUnsaved.Visibility);

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
        editor.TxtConnectionName.Text = "Quiet 2";
        bool? couldLeave = null;
        _onApply = async () => couldLeave = await page.TryLeaveAsync();

        Assert.True(Await(editor.SaveAsync(connect: true)), editor.TxtStatus.Text);

        Assert.Equal([true], _applied);
        Assert.True(couldLeave);
        Assert.Empty(_asked);
        Assert.Equal(_quiet.Id, _settings!.Load().ActiveConnectionId);
    });

    [Fact]
    public void Save_KeepsAMuteMadeElsewhere_WhileThePageWasOpen() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_quiet.Id));
        var editor = Editor(page);

        // From the tray menu, say, after the page was opened.
        _settings!.SetNotificationsMuted(_quiet.Id, muted: true);
        editor.TxtConnectionName.Text = "Quiet 2";
        Assert.True(Await(editor.SaveAsync(connect: false)), editor.TxtStatus.Text);

        Assert.False(_settings.Load().NotifiesFor(_quiet.Id));
    });

    [Fact]
    public void UnsavedChanges_StayWhenMovingBetweenTabs_AndDiscardPutsBackWhatsSaved() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_quiet.Id));
        var editor = Editor(page);

        editor.TxtConnectionName.Text = "Renamed";
        editor.SelectTab(ConnectionTab.Advanced);
        UiThread.Layout(page, 1200, 900);
        editor.TxtConnectTimeout.Text = "45";
        editor.SelectTab(ConnectionTab.Connection);
        UiThread.Layout(page, 1200, 900);

        Assert.Equal("Renamed", editor.TxtConnectionName.Text);
        Assert.Equal("45", editor.TxtConnectTimeout.Text);
        Assert.Equal("●  Unsaved changes on Connection and Advanced", editor.TxtUnsaved.Text);
        Assert.True(editor.BtnDiscard.IsEnabled);

        Click(editor.BtnDiscard);

        Assert.False(editor.IsDirty);
        Assert.Equal("Quiet", editor.TxtConnectionName.Text);
        Assert.Equal("30", editor.TxtConnectTimeout.Text);
        Assert.False(editor.BtnDiscard.IsEnabled);
        Assert.False(editor.BtnSave.IsEnabled);
    });

    [Fact]
    public void TypingAValueAndPuttingItBack_LeavesNothingUnsaved() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_quiet.Id));
        var editor = Editor(page);

        editor.TxtConnectionName.Text = "Something else";
        editor.TxtConnectionName.Text = "Quiet";

        Assert.False(editor.IsDirty);
    });

    [Fact]
    public void LeavingWithUnsavedChanges_Asks_CancelStays_AndNoDiscards() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_quiet.Id));
        var editor = Editor(page);
        editor.TxtConnectionName.Text = "Renamed";

        _answer = MessageBoxResult.Cancel;
        Assert.False(Await(page.TryLeaveAsync()));
        UiThread.Wait(page.OpenAsync(new SettingsLocation(SettingsCategory.Alerts)));

        Assert.Equal(SettingsCategory.Connections, page.Category);
        Assert.Same(editor, Editor(page));
        Assert.True(page.MenuItemFor(SettingsCategory.Connections).IsChecked);
        Assert.Equal(2, _asked.Count);
        Assert.All(_asked, a => Assert.Equal(MessageBoxButton.YesNoCancel, a.Buttons));
        Assert.StartsWith("Save the changes to \"Quiet\"?", _asked[0].Text);

        _answer = MessageBoxResult.No;
        UiThread.Wait(page.OpenAsync(new SettingsLocation(SettingsCategory.Alerts)));

        Assert.Equal(SettingsCategory.Alerts, page.Category);
        Assert.Equal("Quiet", _settings!.Load().Connections.Single(c => c.Id == _quiet.Id).Name);
    });

    [Fact]
    public void LeavingWithUnsavedChanges_AndChoosingSave_SavesThemFirst() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_quiet.Id, ConnectionTab.Monitoring));
        var editor = Editor(page);
        editor.ChkMonitorInBackground.IsChecked = true;

        _answer = MessageBoxResult.Yes;
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
        editor.TxtConnectionName.Text = "Renamed";
        _answer = MessageBoxResult.No;

        editor.LnkBack.RaiseEvent(new RoutedEventArgs(System.Windows.Documents.Hyperlink.ClickEvent));

        Assert.Null(page.Connections!.Editor);
        Assert.Single(_asked);
    });

    [Fact]
    public void Remove_AsksFirst_ThenShowsTheCardsWithoutIt() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_quiet.Id));
        var editor = Editor(page);

        _answer = MessageBoxResult.No;
        Assert.False(Await(editor.RemoveAsync()));
        Assert.Equal(3, _settings!.Load().Connections.Count);

        _answer = MessageBoxResult.Yes;
        Assert.True(Await(editor.RemoveAsync()));

        Assert.Equal(2, _settings.Load().Connections.Count);
        Assert.Null(page.Connections!.Editor);
        Assert.Equal(["Down", "Hot"], page.Connections.CardsShown.Select(c => c.Card.Name));
        Assert.Equal("✓  Removed \"Quiet\".", page.Connections.TxtStatus.Text);
        Assert.Equal(MessageBoxButton.YesNo, _asked[^1].Buttons);
    });

    // ── Health thresholds ─────────────────────────────────────────────────────

    [Fact]
    public void TheThresholdsTab_PreviewsTheLatestSample_OnWhatsTyped() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_hot.Id, ConnectionTab.Thresholds));
        var editor = Editor(page);
        UiThread.Layout(page, 1200, 900);

        // The defaults, read-only, with the sample graded on them: CPU 99 % is critical.
        Assert.True(editor.ConnectionThresholds.IsReadOnly);
        Assert.Equal(("99 %", "−20"), editor.ConnectionThresholds.Cells(HealthIndicator.Cpu));
        Assert.Equal(Visibility.Collapsed, editor.TxtPreview.Visibility);

        editor.ChkCustomThresholds.IsChecked = true;
        editor.ConnectionThresholds.Box(HealthIndicator.Cpu, critical: true).Text = "100";
        editor.ConnectionThresholds.Box(HealthIndicator.Cpu).Text = "99.5";

        Assert.False(editor.ConnectionThresholds.IsReadOnly);
        Assert.Equal(Visibility.Visible, editor.TxtPreview.Visibility);
        Assert.Equal("On the latest sample, Hot would score 80 → 100", editor.TxtPreview.Text);
        Assert.Equal(("99 %", "0"), editor.ConnectionThresholds.Cells(HealthIndicator.Cpu));

        // Turning it off again shows the defaults: nothing left to save.
        editor.ChkCustomThresholds.IsChecked = false;
        Assert.False(editor.IsDirty);
        Assert.Equal("75", editor.ConnectionThresholds.Box(HealthIndicator.Cpu).Text);
    });

    [Fact]
    public void WithNoSample_ThereIsNoPreview() => UiThread.Run(() =>
    {
        var page   = Open(SettingsLocation.Connection(_quiet.Id, ConnectionTab.Thresholds));
        var editor = Editor(page);

        editor.ChkCustomThresholds.IsChecked = true;
        editor.ConnectionThresholds.Box(HealthIndicator.Cpu).Text = "50";

        Assert.Equal(Visibility.Collapsed, editor.TxtPreview.Visibility);
        Assert.Equal(("—", "—"), editor.ConnectionThresholds.Cells(HealthIndicator.Cpu));
    });

    [Fact]
    public void TheDefaultThresholds_SaveOnlyWithSave_AndNotWhenHalfTyped() => UiThread.Run(() =>
    {
        var page = Open(new SettingsLocation(SettingsCategory.Thresholds));
        var category = Assert.IsType<ThresholdsCategory>(page.Shown);
        var cpu = category.GlobalThresholds.Box(HealthIndicator.Cpu);

        cpu.Text = "abc";
        Assert.True(category.IsDirty);
        Assert.True(category.BtnSave.IsEnabled);
        Assert.Equal(75, _settings!.Load().HealthThresholds.CpuPct!.Warning);

        Assert.False(category.Save());
        Assert.Equal(75, _settings.Load().HealthThresholds.CpuPct!.Warning);

        cpu.Text = "70";
        Assert.True(category.Save());
        Assert.Equal(70, _settings.Load().HealthThresholds.CpuPct!.Warning);
        Assert.False(category.IsDirty);
        Assert.Equal(70, _monitoring!.Get(_hot.Id)!.Thresholds.CpuPct!.Warning);
    });

    [Fact]
    public void LeavingTheDefaultThresholds_HalfTyped_Asks() => UiThread.Run(() =>
    {
        var page = Open(new SettingsLocation(SettingsCategory.Thresholds));
        var category = Assert.IsType<ThresholdsCategory>(page.Shown);
        category.GlobalThresholds.Box(HealthIndicator.Cpu).Text = "70";

        _answer = MessageBoxResult.Cancel;
        UiThread.Wait(page.OpenAsync(new SettingsLocation(SettingsCategory.Alerts)));

        Assert.Equal(SettingsCategory.Thresholds, page.Category);
        Assert.Single(_asked);
    });

    // ── Categories for the whole app ──────────────────────────────────────────

    [Fact]
    public void AlertsAndChat_ListsTheMutedConnections_AndItsSwitchMutesThemAll() => UiThread.Run(() =>
    {
        var page = Open(new SettingsLocation(SettingsCategory.Alerts));
        var alerts = Assert.IsType<AlertsCategory>(page.Shown);
        Assert.True(alerts.ChkNotifications.IsChecked);
        Assert.Equal(Visibility.Collapsed, alerts.TxtMuted.Visibility);

        _settings!.SetNotificationsMuted(_quiet.Id, muted: true);
        UiThread.Wait(page.OpenAsync(new SettingsLocation(SettingsCategory.Appearance)));
        UiThread.Wait(page.OpenAsync(new SettingsLocation(SettingsCategory.Alerts)));
        UiThread.Layout(page, 1200, 900);

        Assert.True(alerts.ChkNotifications.IsChecked);
        Assert.Equal("Muted: Quiet", Text(alerts.TxtMuted.Inlines));
        Assert.Single(alerts.TxtMuted.Inlines.OfType<System.Windows.Documents.Hyperlink>());

        alerts.ChkNotifications.IsChecked = false;
        Click(alerts.ChkNotifications);

        var store = _settings.Load();
        Assert.All(store.Connections, c => Assert.False(store.NotifiesFor(c.Id)));
        Assert.False(alerts.ChkNotifications.IsChecked);
    });

    [Fact]
    public void CommandTimeout_SavesOnItsOwn_AndIsApplied() => UiThread.Run(() =>
    {
        var page = Open(new SettingsLocation(SettingsCategory.Monitoring));
        var monitoring = Assert.IsType<MonitoringCategory>(page.Shown);
        Assert.Equal("30", monitoring.TxtTimeout.Text);

        monitoring.TxtTimeout.Text = "0";
        UiThread.Wait(monitoring.SaveTimeoutAsync());
        Assert.Equal(30, _settings!.Load().CommandTimeoutSeconds);
        Assert.StartsWith("✗", monitoring.TxtMonitoringStatus.Text);

        monitoring.TxtTimeout.Text = "90";
        UiThread.Wait(monitoring.SaveTimeoutAsync());

        Assert.Equal(90, _settings.Load().CommandTimeoutSeconds);
        Assert.Equal([false], _applied);

        // Saving a connection keeps it.
        var editor = new ConnectionEditor(TestContext(), _quiet.Id);
        editor.TxtConnectionName.Text = "Quiet 2";
        Assert.True(Await(editor.SaveAsync(connect: false)));
        Assert.Equal(90, _settings.Load().CommandTimeoutSeconds);
    });

    private SettingsContext TestContext() =>
        new(_settings!, _monitoring!, _chat!, Apply, (_, _, _, _) => _answer, _ => { }, _ => { });

    private sealed class NoSender : Engine.ChatNotifications.IWebhookSender
    {
        public Task<Engine.ChatNotifications.WebhookResult> SendAsync(Uri url, string json, Engine.ChatNotifications.ChatService service,
                                                                      CancellationToken cancel = default) =>
            throw new InvalidOperationException("No chat in these tests.");

        public Task<Engine.ChatNotifications.WebhookResult> PingAsync(Uri url, CancellationToken cancel = default) =>
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
