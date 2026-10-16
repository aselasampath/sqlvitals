using System.ComponentModel;
using System.IO;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.Tests.Fakes;
using SqlVitals.Desktop.ViewModels;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Desktop.ViewModels.Settings;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// Settings' view model and its categories' (#190), on their own: no page, no UI thread. Moving
/// between categories, asking before leaving one with unsaved changes, and what the categories that
/// save as they change do when a save is refused.
/// </summary>
public sealed class SettingsViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "SqlVitalsTests", Guid.NewGuid().ToString("N"));
    private readonly ConnectionSettingsService _settings;
    private readonly MonitoringManager _monitoring;
    private readonly ChatNotificationService _chat;
    private readonly FakeDialogService _dialogs = new();
    private readonly FakeUiDispatcher _ui = new();
    private readonly FakeClipboard _clipboard = new();
    private readonly ConnectionSettings _prod = new() { Name = "Prod", Server = "sql01", Database = "Sales", Authentication = SqlAuthMode.Windows };

    public SettingsViewModelTests()
    {
        _settings = new ConnectionSettingsService(_dir);
        _settings.Upsert(_prod);
        _monitoring = new MonitoringManager(new HistoryWriter(Path.Combine(_dir, "history.db"), (_, _) => { }), (_, _) => new FakeRepository());
        _chat = new ChatNotificationService(new NoSender(), "DBA-PC01", _ => { });
    }

    private SettingsContext Context() =>
        new(_settings, _monitoring, _chat, _ => Task.CompletedTask, _dialogs, clipboard: _clipboard)
        {
            Ui    = _ui,
            Theme = new FakeThemeService(),
        };

    private SettingsViewModel Open(SettingsLocation? location = null) => new(Context(), location);

    // ── Moving between categories ─────────────────────────────────────────

    [Fact]
    public void OpensOnConnections_WithItsCountInTheMenu_AndBuildsEachCategoryOnce()
    {
        var settings = Open();

        Assert.Equal(SettingsCategory.Connections, settings.Category);
        Assert.IsType<ConnectionsCategoryViewModel>(settings.Current);
        Assert.Equal(["SERVERS", "MONITORING", "NOTIFICATIONS", "APP"], settings.Groups.Select(g => g.Heading));
        Assert.Equal([true, false, false, false], settings.Groups.Select(g => g.IsFirst));
        var connections = settings.MenuItemFor(SettingsCategory.Connections);
        Assert.True(connections.IsSelected);
        Assert.Equal(("1", "Connections, 1 saved"), (connections.Count, connections.AutomationName));

        var shown = new List<SettingsCategory>();
        settings.CategoryShown += shown.Add;
        settings.OpenCategoryCommand.Execute(SettingsCategory.Alerts);
        var alerts = settings.Current;
        settings.OpenCategoryCommand.Execute(SettingsCategory.Appearance);
        settings.OpenCategoryCommand.Execute(SettingsCategory.Alerts);

        Assert.Same(alerts, settings.Current);
        Assert.Equal([SettingsCategory.Alerts, SettingsCategory.Appearance, SettingsCategory.Alerts], shown);
        Assert.True(settings.MenuItemFor(SettingsCategory.Alerts).IsSelected);
        Assert.Equal(1, settings.Groups.SelectMany(g => g.Items).Count(i => i.IsSelected));
    }

    [Fact]
    public void LeavingADirtyConnection_Asks_AndCancelKeepsItAndShowsTheMenuAsItWas()
    {
        var settings = Open(SettingsLocation.Connection(_prod.Id));
        var editor   = settings.Connections!.Editor!;
        editor.Name  = "Renamed";
        var alerts   = settings.MenuItemFor(SettingsCategory.Alerts);
        var told     = new List<string?>();
        alerts.PropertyChanged += (_, e) => told.Add(e.PropertyName);

        _dialogs.SaveAnswer = DialogAnswer.Cancel;
        settings.OpenCategoryCommand.Execute(SettingsCategory.Alerts);

        Assert.Equal(SettingsCategory.Connections, settings.Category);
        Assert.Same(editor, settings.Connections.Editor);
        Assert.False(alerts.IsSelected);
        // Told again, so a radio button the user clicked shows the category that stayed.
        Assert.Contains(nameof(SettingsMenuItem.IsSelected), told);
        Assert.Single(_dialogs.Shown);

        _dialogs.SaveAnswer = DialogAnswer.No;
        settings.OpenCategoryCommand.Execute(SettingsCategory.Alerts);
        Assert.Equal(SettingsCategory.Alerts, settings.Category);
        Assert.Equal("Prod", _settings.Load().Connections.Single().Name);
    }

    [Fact]
    public void TheMenu_StaysUsable_WhileLeavingACategorySignsIn()
    {
        var signIn = new TaskCompletionSource<string?>();
        var settings = new SettingsViewModel(new SettingsContext(_settings, _monitoring, _chat, _ => Task.CompletedTask, _dialogs)
        {
            Ui = _ui, Theme = new FakeThemeService(), TestConnection = _ => signIn.Task,
        }, SettingsLocation.Connection(_prod.Id));
        settings.Connections!.Editor!.Server = "sql09";
        _dialogs.SaveAnswer = DialogAnswer.Yes;

        settings.OpenCategoryCommand.Execute(SettingsCategory.Monitoring);

        // Saving waits on the sign-in; the other categories can still be clicked meanwhile.
        Assert.Equal(SettingsCategory.Connections, settings.Category);
        Assert.True(settings.OpenCategoryCommand.CanExecute(SettingsCategory.Alerts));

        signIn.SetResult(null);
        Assert.Equal(SettingsCategory.Monitoring, settings.Category);
        Assert.Equal("sql09", _settings.Load().Connections.Single().Server);
    }

    [Fact]
    public async Task LeavingSettings_AsksTheCategoryOnScreen()
    {
        var settings = Open(new SettingsLocation(SettingsCategory.Thresholds));
        var thresholds = Assert.IsType<ThresholdsCategoryViewModel>(settings.Current);
        Assert.True(await settings.TryLeaveAsync());

        thresholds.Thresholds.Row(HealthIndicator.Cpu).Warning.Text = "70";
        _dialogs.SaveAnswer = DialogAnswer.Cancel;
        Assert.False(await settings.TryLeaveAsync());

        _dialogs.SaveAnswer = DialogAnswer.Yes;
        Assert.True(await settings.TryLeaveAsync());
        Assert.Equal(70, _settings.Load().HealthThresholds.CpuPct!.Warning);
    }

    [Fact]
    public void ADeepLink_ToAConnectionsTab_OpensItsPageOnThatTab()
    {
        var settings = Open(SettingsLocation.Connection(_prod.Id, ConnectionTab.Advanced));

        Assert.Equal((_prod.Id, ConnectionTab.Advanced), (settings.Connections!.Editor!.ConnectionId, settings.Connections.Editor.SelectedTab));
    }

    [Fact]
    public void ALinkInACategory_OpensAnother_AndViewOnHomeAsksTheWindow()
    {
        var settings = Open(SettingsLocation.Connection(_prod.Id));
        var servers  = new List<Guid>();
        settings.ServerRequested += servers.Add;
        var editor = settings.Connections!.Editor!;

        editor.DefaultThresholdsCommand.Execute(null);
        Assert.Equal(SettingsCategory.Thresholds, settings.Category);

        editor.ViewOnHomeCommand.Execute(null);
        Assert.Equal([_prod.Id], servers);
    }

    [Fact]
    public void AMessage_ShowsAboveTheCategory_UntilClosed()
    {
        var settings = new SettingsViewModel(Context(), message: "Switched to UAT.");
        Assert.Equal("Switched to UAT.", settings.Message);

        settings.CloseMessageCommand.Execute(null);
        Assert.Null(settings.Message);

        settings.ShowMessage("  ");
        Assert.Null(settings.Message);
    }

    [Fact]
    public void TheCount_FollowsTheSettings_WhileShown()
    {
        var settings = Open();
        settings.Activate();

        _settings.Upsert(new ConnectionSettings { Name = "UAT", Server = "sql02", Authentication = SqlAuthMode.Windows });
        _ui.RunPosted();
        Assert.Equal("2", settings.MenuItemFor(SettingsCategory.Connections).Count);

        settings.Deactivate();
        _settings.Upsert(new ConnectionSettings { Name = "Dev", Server = "sql03", Authentication = SqlAuthMode.Windows });
        _ui.RunPosted();
        Assert.Equal("2", settings.MenuItemFor(SettingsCategory.Connections).Count);
    }

    // ── First run (#177) ──────────────────────────────────────────────────

    [Fact]
    public void FirstRun_ShowsOnlyWithNothingSaved_AndSkipShowsConnections()
    {
        Assert.Null(Open(SettingsLocation.FirstRunFlow).FirstRun);

        _settings.Remove(_prod.Id);
        var settings = Open(SettingsLocation.FirstRunFlow);
        var flow = settings.FirstRun!;
        Assert.Equal("Step 1 of 3: Connect", flow.StepsName);
        Assert.Equal([true, false, false], flow.Steps.Select(s => s.IsCurrent));
        Assert.True(flow.Editor.IsFirstRun);
        Assert.False(flow.Editor.ShowsSaveAndConnect);
        Assert.Equal("Save & continue", flow.Editor.SaveText);

        flow.SkipCommand.Execute(null);
        Assert.Null(settings.FirstRun);
    }

    [Fact]
    public void FirstRun_StepsShowWhatsDone()
    {
        _settings.Remove(_prod.Id);
        var flow = Open(SettingsLocation.FirstRunFlow).FirstRun!;
        _settings.Upsert(_prod);

        flow.Connected(_prod.Id);

        // No permission check in these tests: straight to Start.
        Assert.Equal(FirstRunStep.Start, flow.Step);
        Assert.Equal(["✓", "✓", "3"], flow.Steps.Select(s => s.Number));
        Assert.Equal("Open Prod's Overview  ›", flow.FinishText);
    }

    // ── Categories that save as they change ───────────────────────────────

    [Fact]
    public void AChoiceThatCantBeSaved_IsPutBackInTheBox()
    {
        var monitoring = new MonitoringCategoryViewModel(Context());
        var saved = monitoring.RetentionDays;
        var shorter = monitoring.RetentionChoices.Select(c => c.Value).Where(d => d < saved).Max();
        var told = new List<string?>();
        monitoring.PropertyChanged += (_, e) => told.Add(e.PropertyName);

        _dialogs.ConfirmAnswer = false;
        monitoring.RetentionDays = shorter;

        Assert.Equal(saved, monitoring.RetentionDays);
        Assert.Equal(saved, _settings.Load().HistoryRetentionDays);
        Assert.Empty(told);
        _ui.RunPosted();
        Assert.Equal([nameof(MonitoringCategoryViewModel.RetentionDays)], told);

        _dialogs.ConfirmAnswer = true;
        monitoring.RetentionDays = shorter;
        Assert.Equal(shorter, _settings.Load().HistoryRetentionDays);
        Assert.StartsWith("✓  Saved: a snapshot every", monitoring.HistoryStatus.Text);
    }

    [Fact]
    public void ThresholdsCategory_SaveIsDisabledWhileABoxCantBeRead_AndDiscardPutsThemBack()
    {
        var thresholds = new ThresholdsCategoryViewModel(Context());
        Assert.False(thresholds.IsDirty);
        Assert.False(thresholds.SaveThresholdsCommand.CanExecute(null));

        var cpu = thresholds.Thresholds.Row(HealthIndicator.Cpu).Warning;
        cpu.Text = "abc";
        Assert.True(thresholds.IsDirty);
        Assert.NotNull(thresholds.Problem);
        Assert.False(thresholds.SaveThresholdsCommand.CanExecute(null));
        Assert.True(cpu.IsEdited);

        cpu.Text = "70";
        Assert.Null(thresholds.Problem);
        Assert.True(thresholds.SaveThresholdsCommand.CanExecute(null));

        thresholds.DiscardThresholdsCommand.Execute(null);
        Assert.Equal("75", cpu.Text);
        Assert.False(thresholds.IsDirty);

        cpu.Text = "70";
        thresholds.SaveThresholdsCommand.Execute(null);
        Assert.Equal(70, _settings.Load().HealthThresholds.CpuPct!.Warning);
        Assert.StartsWith("✓  Saved.", thresholds.Status.Text);
    }

    [Fact]
    public void AChannelsWebhook_IsInNoBoundPropertyUntilShow_AndSavesAsItWasMeanwhile()
    {
        const string url = "https://hooks.slack.com/services/T000/B000/XXXXXXXXXXXXXXXXXXXXXXXX";
        _settings.SaveChatChannel(new ChatChannel { Name = "DBA team", Service = ChatService.Slack, WebhookUrl = url, AllConnections = true });
        var alerts = new AlertsCategoryViewModel(Context());

        alerts.Channels.Single().EditCommand!.Execute(null);
        var form = alerts.Form!;
        Assert.False(form.IsUrlShown);
        Assert.Equal(string.Empty, form.Url);
        Assert.DoesNotContain("XXXXXXXX", form.MaskedUrl);

        // Saved without Show: the URL as it was.
        form.Name = "DBA";
        alerts.SaveChannelCommand.Execute(null);
        Assert.Equal(url, _settings.Load().ChatChannels.Single().WebhookUrl);
        Assert.Equal("DBA", _settings.Load().ChatChannels.Single().Name);

        alerts.Form!.ShowUrlCommand.Execute(null);
        Assert.Equal(url, alerts.Form.Url);
        Assert.Equal(ChatService.Slack, alerts.Form.Service);
    }

    [Fact]
    public void RemovingAChannel_AsksFirst()
    {
        _settings.SaveChatChannel(new ChatChannel { Name = "DBA team", Service = ChatService.Slack,
                                                    WebhookUrl = "https://hooks.slack.com/services/T000/B000/XXXXXXXXXXXXXXXXXXXXXXXX", AllConnections = true });
        var alerts = new AlertsCategoryViewModel(Context());
        alerts.Channels.Single().EditCommand!.Execute(null);

        _dialogs.ConfirmAnswer = false;
        alerts.RemoveChannelCommand.Execute(null);
        Assert.Single(_settings.Load().ChatChannels);
        Assert.NotNull(alerts.Form);

        _dialogs.ConfirmAnswer = true;
        alerts.RemoveChannelCommand.Execute(null);
        Assert.Empty(_settings.Load().ChatChannels);
        Assert.Null(alerts.Form);
        Assert.Equal("✓  Removed \"DBA team\".", alerts.ChatStatus.Text);
    }

    [Fact]
    public void TheNotificationsSwitch_MutesAndUnmutesEveryConnection()
    {
        var alerts = new AlertsCategoryViewModel(Context());
        Assert.True(alerts.Notifications);

        alerts.ToggleNotificationsCommand.Execute(null);
        Assert.False(_settings.Load().NotifiesFor(_prod.Id));
        Assert.False(alerts.Notifications);
        Assert.Equal("Every connection is muted: ", alerts.MutedText);
        Assert.Equal("Prod", alerts.Muted.Single().Name);

        alerts.ToggleNotificationsCommand.Execute(null);
        Assert.True(_settings.Load().NotifiesFor(_prod.Id));
        Assert.Empty(alerts.Muted);
    }

    [Fact]
    public void CopySteps_CopiesTheHeartbeatSetup()
    {
        var alerts = new AlertsCategoryViewModel(Context());
        alerts.AddChannelCommand.Execute(null);

        alerts.Form!.HeartbeatIndex = 1;
        alerts.Form.CopyStepsCommand.Execute(null);

        Assert.Equal(alerts.Form.HeartbeatSteps, _clipboard.Text);
        Assert.Equal("✓  Copied the heartbeat setup steps.", alerts.Form.Message.Text);
    }

    [Fact]
    public void Appearance_TogglesTheTheme_AndSavesTheDensity()
    {
        var theme = new FakeThemeService(AppTheme.Dark);
        var applied = new List<GridDensity>();
        var appearance = new AppearanceCategoryViewModel(new SettingsContext(_settings, _monitoring, _chat, _ => Task.CompletedTask, _dialogs)
        {
            Ui = _ui, Theme = theme, ApplyDensity = applied.Add,
        });
        var changed = 0;
        appearance.ThemeChanged += () => changed++;
        Assert.Equal("☀  Light", appearance.ThemeButtonText);

        appearance.ToggleThemeCommand.Execute(null);
        Assert.Equal((AppTheme.Light, 1), (theme.Current, changed));

        appearance.IsCompact = true;
        Assert.Equal([GridDensity.Compact], applied);
        Assert.Equal(GridDensity.Compact, _settings.Load().GridDensity);
        Assert.False(appearance.IsComfortable);
    }

    [Fact]
    public void Activity_OpensTheLogFolder_AndSaysWhenItCant()
    {
        var opened = new List<string>();
        var activity = new ActivityCategoryViewModel(new SettingsContext(_settings, _monitoring, _chat, _ => Task.CompletedTask, _dialogs)
        {
            Ui = _ui, Theme = new FakeThemeService(), OpenFolder = opened.Add,
        });

        activity.OpenLogFolderCommand.Execute(null);
        Assert.Equal([FileLog.Directory], opened);

        var failing = new ActivityCategoryViewModel(new SettingsContext(_settings, _monitoring, _chat, _ => Task.CompletedTask, _dialogs)
        {
            Ui = _ui, Theme = new FakeThemeService(), OpenFolder = _ => throw new IOException("No Explorer here."),
        });
        failing.OpenLogFolderCommand.Execute(null);
        Assert.Equal("✗  Could not open the log folder: No Explorer here.", failing.LogStatus.Text);
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
        _monitoring.Dispose();
        _chat.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
