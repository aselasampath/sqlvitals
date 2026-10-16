using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ChatNotifications;

namespace SqlVitals.Desktop.ViewModels.Settings;

/// <summary>
/// Settings → Alerts &amp; chat (#131): when an alert starts, Windows notifications for every
/// connection with the muted ones listed, and the Slack and Teams channels as cards (#120), with a
/// form to add or edit one. Each saves as it changes; the form with its own Save.
/// </summary>
public sealed partial class AlertsCategoryViewModel : SettingsCategoryViewModel
{
    private readonly SettingsContext _context;
    private int _alertSamples;

    internal AlertsCategoryViewModel(SettingsContext context)
    {
        _context = context;
        var store = context.Service.Load();
        _alertSamples   = store.AlertSamples;
        ChannelsDropped = store.ChatChannelsDropped;
        ShowNotifications(store);
        ShowChannels(store);
    }

    public override string Subtitle => SettingsNav.Info(SettingsCategory.Alerts).Subtitle;

    // The channel form saves with its own button; leaving with it open drops what's typed, as before.
    public override Task<bool> TryLeaveAsync() => Task.FromResult(true);

    // A connection muted on its own tab meanwhile, or a channel's status changed.
    public override void OnShownAgain() => Reload();

    protected override void OnActivated()
    {
        _context.Chat.StatusChanged += OnChatStatusChanged;
        _context.Service.Changed    += OnSettingsChanged;
    }

    protected override void OnDeactivated()
    {
        _context.Chat.StatusChanged -= OnChatStatusChanged;
        _context.Service.Changed    -= OnSettingsChanged;
    }

    // A connection renamed, muted or removed, or a channel saved: the names and the muted list follow.
    private void OnSettingsChanged(IReadOnlyList<ActivityEntry> rows) => _context.Ui.Post(Reload);

    private void OnChatStatusChanged(Guid channelId) => _context.Ui.Post(() => ShowChannels(_context.Service.Load()));

    private void Reload()
    {
        var store = _context.Service.Load();
        ShowNotifications(store);
        ShowChannels(store);
    }

    // ── Alerts ────────────────────────────────────────────────────────────────

    public IReadOnlyList<Choice<int>> AlertSampleChoices { get; } =
        AlertSettings.SampleChoices.Select(n => new Choice<int>(n, SamplesText(n))).ToList();

    /// <summary>Samples in a row past a threshold before an alert starts: saved and applied as it's picked.</summary>
    public int AlertSamples
    {
        get => _alertSamples;
        set
        {
            if (value == _alertSamples)
                return;
            try
            {
                _context.Service.SetAlertSettings(value);
                var store = _context.Service.Load();
                _context.Monitoring.ApplyAlertSettings(store);
                _alertSamples = store.AlertSamples;
                OnPropertyChanged();
                AlertsStatus.Show($"Saved: an alert starts after {SamplesText(store.AlertSamples)} in a row past a threshold, " +
                                  "and ends after as many back to normal.", success: true);
            }
            catch (Exception ex)
            {
                AlertsStatus.Show($"Could not save the alert settings: {ex.Message}", success: false);
                // Later: the box picked it, and only puts the saved one back once its own change is done.
                _context.Ui.Post(() => OnPropertyChanged(nameof(AlertSamples)));
            }
        }
    }

    private static string SamplesText(int samples) => samples == 1 ? "1 sample" : $"{samples} samples";

    /// <summary>What saving the alert or notification settings said.</summary>
    public StatusLine AlertsStatus { get; } = new();

    // ── Windows notifications ─────────────────────────────────────────────────

    /// <summary>On while any connection notifies.</summary>
    [ObservableProperty]
    private bool _notifications;

    /// <summary>There is a connection to mute or unmute.</summary>
    [ObservableProperty]
    private bool _canNotify;

    /// <summary>"Muted: ", "Every connection is muted: ", "No saved connections yet."; empty with none muted.</summary>
    [ObservableProperty]
    private string _mutedText = string.Empty;

    /// <summary>The muted connections, each a link to its Monitoring &amp; alerts tab.</summary>
    [ObservableProperty]
    private IReadOnlyList<MutedConnection> _muted = [];

    private void ShowNotifications(ConnectionStore store)
    {
        var muted = store.Connections
            .Where(c => !store.NotifiesFor(c.Id))
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        CanNotify     = store.Connections.Count > 0;
        Notifications = store.Connections.Count > 0 && muted.Count < store.Connections.Count;
        // The switch may show a click this didn't take: put it back.
        OnPropertyChanged(nameof(Notifications));

        MutedText = store.Connections.Count == 0 ? "No saved connections yet."
                  : muted.Count == 0             ? string.Empty
                  : muted.Count == store.Connections.Count ? "Every connection is muted: " : "Muted: ";
        Muted = muted.Select((c, i) =>
        {
            var id = c.Id;
            return new MutedConnection(c.DisplayName, i < muted.Count - 1 ? ", " : string.Empty,
                                       new RelayCommand(() => _context.Open(SettingsLocation.Connection(id, ConnectionTab.Monitoring))));
        }).ToList();
    }

    /// <summary>The switch: off mutes every connection, on unmutes them all. There is no setting of its own (#131).</summary>
    [RelayCommand]
    private void ToggleNotifications()
    {
        var notify = !Notifications;
        var store  = _context.Service.Load();
        try
        {
            foreach (var conn in store.Connections.Where(c => store.NotifiesFor(c.Id) != notify))
                _context.Service.SetNotificationsMuted(conn.Id, muted: !notify);
            AlertsStatus.Show(notify
                    ? "Saved: alerts on every connection raise a Windows notification."
                    : "Saved: every connection is muted. Its alerts are still listed on the Alerts page and posted to its chat channels.",
                success: true);
        }
        catch (Exception ex)
        {
            AlertsStatus.Show($"Could not save the notification setting: {ex.Message}", success: false);
        }
        ShowNotifications(_context.Service.Load());
    }

    // ── Chat channels (#120) ──────────────────────────────────────────────────

    /// <summary>The channel cards, by name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChannels))]
    private IReadOnlyList<ChatChannelCard> _channels = [];

    public bool HasChannels => Channels.Count > 0;

    /// <summary>The saved channels couldn't be read on this PC or account, so they were dropped.</summary>
    public bool ChannelsDropped { get; }

    /// <summary>What a channel's save, removal or test said.</summary>
    public StatusLine ChatStatus { get; } = new();

    /// <summary>The form to add or edit a channel; null while it's closed.</summary>
    [ObservableProperty]
    private ChatChannelForm? _form;

    private void ShowChannels(ConnectionStore store)
    {
        var now = DateTime.Now;
        Channels = store.ChatChannels
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c =>
            {
                var card = ChatChannelCard.From(c, _context.Chat.Status(c.Id), _context.Chat.ConnectionsText(c), now);
                return card with
                {
                    SendTestCommand = new AsyncRelayCommand(() => SendTestAsync(card)),
                    EditCommand     = new RelayCommand(() => EditChannel(card.Id)),
                };
            })
            .ToList();
    }

    [RelayCommand]
    private void AddChannel()
    {
        OpenForm(null, _context.Service.Load());
        ChatStatus.Clear();
    }

    private void EditChannel(Guid id)
    {
        var store = _context.Service.Load();
        if (store.ChatChannels.FirstOrDefault(c => c.Id == id) is { } channel)
        {
            OpenForm(channel, store);
            ChatStatus.Clear();
        }
    }

    private void OpenForm(ChatChannel? channel, ConnectionStore store)
    {
        Form = new ChatChannelForm(channel, store.Connections, _context.Clipboard);
        Form.Focus("Name");
    }

    [RelayCommand]
    private void CloseChannel() => Form = null;

    [RelayCommand]
    private void SaveChannel()
    {
        if (Form is not { } form)
            return;
        var store   = _context.Service.Load();
        var id      = form.ChannelId ?? Guid.NewGuid();
        var name    = form.Name.Trim();
        var url     = form.UrlToSave;

        if (name.Length == 0)
        {
            form.Message.Show("Enter a name for the channel, e.g. DBA team (Teams).", success: false);
            form.Focus("Name");
            return;
        }
        if (store.ChatChannels.Any(c => c.Id != id && string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            form.Message.Show($"A channel named \"{name}\" already exists. Enter a different name.", success: false);
            form.Focus("Name");
            return;
        }

        var chosen = form.Connections.Where(i => i.Chosen).Select(i => i.Id).ToList();
        if (!form.AllConnections && chosen.Count == 0)
        {
            form.Message.Show("Tick at least one connection, or choose All monitored connections.", success: false);
            return;
        }

        var check = WebhookUrl.Check(url, form.Service, store.ChatChannels, id);
        if (!check.IsValid)
        {
            form.Message.Show(check.Error ?? string.Empty, success: false);
            form.ShowUrl();
            return;
        }

        var pingUrl = form.PingUrlToSave;
        if (HeartbeatPing.Check(pingUrl, url) is { } pingError)
        {
            form.Message.Show(pingError, success: false);
            form.ShowUrl();
            form.Focus("PingUrl");
            return;
        }
        var heartbeat = form.ChosenHeartbeat();

        var channel = new ChatChannel
        {
            Id               = id,
            Name             = name,
            Service          = form.Service,
            WebhookUrl       = url,
            Enabled          = form.Enabled,
            AllConnections   = form.AllConnections,
            ConnectionIds    = chosen,
            CriticalOnly     = form.CriticalOnly,
            PostEnds         = form.PostEnds,
            HeartbeatEvery   = heartbeat.Every,
            HeartbeatHours   = heartbeat.Every == HeartbeatEvery.Hours ? heartbeat.Hours : HeartbeatSchedule.EveryHours(6).Hours,
            HeartbeatAt      = heartbeat.At,
            HeartbeatPingUrl = pingUrl,
        };

        try
        {
            _context.Service.SaveChatChannel(channel);
        }
        catch (Exception ex)
        {
            form.Message.Show($"Could not save the channel: {ex.Message}", success: false);
            return;
        }

        // Opened again as saved: the URLs are masked once more.
        var saved = _context.Service.Load();
        ShowChannels(saved);
        if (saved.ChatChannels.FirstOrDefault(c => c.Id == id) is { } savedChannel)
            Form = new ChatChannelForm(savedChannel, saved.Connections, _context.Clipboard);

        ChatStatus.Show(
            $"Saved \"{name}\"" + (channel.Enabled ? "." : ", turned off.") +
            (check.Warning is { } warning ? $" {warning}" : " Send a test message to check it reaches the channel."),
            success: check.Warning is null ? true : null);
    }

    [RelayCommand]
    private void RemoveChannel()
    {
        if (Form?.ChannelId is not { } id)
            return;
        var name = _context.Service.Load().ChatChannels.FirstOrDefault(c => c.Id == id)?.Name ?? Form.Name.Trim();

        if (!_context.Dialogs.Confirm(
                $"Remove the chat channel \"{name}\"?\n\nSqlVitals stops posting to it. The webhook itself stays in Slack or Teams until you delete it there.",
                "Remove Chat Channel"))
            return;

        try
        {
            _context.Service.RemoveChatChannel(id);
        }
        catch (Exception ex)
        {
            ChatStatus.Show($"Could not remove the channel: {ex.Message}", success: false);
            return;
        }

        Form = null;
        ShowChannels(_context.Service.Load());
        ChatStatus.Show($"Removed \"{name}\".", success: true);
    }

    // Posts a test message to the channel as it's saved; its button waits meanwhile.
    [ReportsItsErrors]
    private async Task SendTestAsync(ChatChannelCard card)
    {
        var channel = _context.Chat.Channels.FirstOrDefault(c => c.Id == card.Id);
        if (channel is null)
        {
            ChatStatus.Show($"\"{card.Name}\" isn't saved any more.", success: false);
            return;
        }

        ChatStatus.Show($"Sending a test message to \"{card.Name}\"…", success: null);
        try
        {
            var result = await _context.Chat.SendTestAsync(card.Id);
            ChatStatus.Show($"\"{card.Name}\": {result.Describe(channel.Service)}", success: result.IsSuccess);
        }
        catch (Exception ex)
        {
            ChatStatus.Show($"Could not send the test message: {ex.Message}", success: false);
        }
    }
}
