using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.Home;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>
/// Settings → Alerts &amp; chat (#131): when an alert starts, Windows notifications for every
/// connection with the muted ones listed, and the Slack and Teams channels as cards (#120).
/// </summary>
internal partial class AlertsCategory : UserControl, ISettingsCategory
{
    // The narrowest a channel card gets before they go one a row.
    private const double MinChannelWidth = 300;

    public static readonly DependencyProperty ChannelWidthProperty =
        DependencyProperty.Register(nameof(ChannelWidth), typeof(double), typeof(AlertsCategory), new PropertyMetadata(MinChannelWidth + 12));

    private readonly SettingsContext _context;

    // Set while the alert choice is filled in or put back in code, so it isn't saved.
    private bool _isLoadingAlertSettings;
    private int  _savedAlertSamples;

    // The chat channel the form is editing (#120); null while adding a new one.
    private Guid? _editingChannelId;

    // The saved URL of the channel being edited. It goes into the URL box only when the user
    // presses Show, so no control (or UI Automation client) holds it before then.
    private string? _savedChannelUrl;
    private bool    _channelUrlShown;
    private bool    _isFillingChatUrl;

    public AlertsCategory(SettingsContext context)
    {
        _context = context;
        InitializeComponent();
        TxtSubtitle.Text = SettingsNav.Info(SettingsCategory.Alerts).Subtitle;

        var store = _context.Service.Load();
        LoadAlertSettings(store);
        ShowNotifications(store);
        PopulateChatChannels(store);
        TxtChatChannelsDropped.Visibility = store.ChatChannelsDropped ? Visibility.Visible : Visibility.Collapsed;

        ChannelCards.SizeChanged += (_, _) => UpdateChannelWidth();
        Loaded += (_, _) =>
        {
            _context.Chat.StatusChanged += OnChatStatusChanged;
            _context.Service.Changed    += OnSettingsChanged;
        };
        Unloaded += (_, _) =>
        {
            _context.Chat.StatusChanged -= OnChatStatusChanged;
            _context.Service.Changed    -= OnSettingsChanged;
        };
    }

    public double ChannelWidth
    {
        get => (double)GetValue(ChannelWidthProperty);
        set => SetValue(ChannelWidthProperty, value);
    }

    /// <summary>The channel cards shown.</summary>
    internal IReadOnlyList<ChatChannelCard> ChannelsShown => ChannelCards.ItemsSource as IReadOnlyList<ChatChannelCard> ?? [];

    // The channel form saves with its own button; leaving with it open drops what's typed, as before.
    public Task<bool> TryLeaveAsync() => Task.FromResult(true);

    // A connection muted on its own tab meanwhile, or a channel's status changed.
    public void OnShownAgain()
    {
        var store = _context.Service.Load();
        ShowNotifications(store);
        PopulateChatChannels(store);
    }

    private void UpdateChannelWidth()
    {
        var width   = Math.Max(0, ChannelCards.ActualWidth);
        if (!double.IsFinite(width) || width == 0)
            return;
        var columns = width >= 2 * (MinChannelWidth + 12) ? 2 : 1;
        ChannelWidth = Math.Floor(width / columns);
    }

    // A connection renamed, muted or removed, or a channel saved: the names and the muted list follow.
    private void OnSettingsChanged(IReadOnlyList<ActivityEntry> rows) => Dispatcher.InvokeAsync(() =>
    {
        var store = _context.Service.Load();
        ShowNotifications(store);
        PopulateChatChannels(store);
    });

    // ── Alerts ────────────────────────────────────────────────────────────────

    private void LoadAlertSettings(ConnectionStore store)
    {
        _isLoadingAlertSettings = true;
        try
        {
            foreach (var samples in AlertSettings.SampleChoices)
                CmbAlertSamples.Items.Add(new ComboBoxItem { Content = SamplesText(samples), Tag = samples });
            ShowAlertSettings(store.AlertSamples);
        }
        finally
        {
            _isLoadingAlertSettings = false;
        }
    }

    private void ShowAlertSettings(int samples)
    {
        _savedAlertSamples           = samples;
        CmbAlertSamples.SelectedItem = MonitoringCategory.FindChoice(CmbAlertSamples, samples);
    }

    private void AlertSetting_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingAlertSettings || CmbAlertSamples.SelectedItem is not ComboBoxItem { Tag: int samples } ||
            samples == _savedAlertSamples)
            return;

        try
        {
            _context.Service.SetAlertSettings(samples);
            var store = _context.Service.Load();
            _context.Monitoring.ApplyAlertSettings(store);
            _savedAlertSamples = store.AlertSamples;
            SettingsContext.ShowStatus(TxtAlertsStatus,
                $"Saved: an alert starts after {SamplesText(store.AlertSamples)} in a row past a threshold, " +
                "and ends after as many back to normal.",
                success: true);
        }
        catch (Exception ex)
        {
            SettingsContext.ShowStatus(TxtAlertsStatus, $"Could not save the alert settings: {ex.Message}", success: false);
            // Deferred: changing the selection inside its own SelectionChanged handler can leave
            // the ComboBox showing the rejected choice.
            Dispatcher.BeginInvoke(() =>
            {
                _isLoadingAlertSettings = true;
                try { ShowAlertSettings(_savedAlertSamples); }
                finally { _isLoadingAlertSettings = false; }
            });
        }
    }

    private static string SamplesText(int samples) => samples == 1 ? "1 sample" : $"{samples} samples";

    // ── Windows notifications ─────────────────────────────────────────────────

    // On while any connection notifies; the muted ones listed, each a link to its Monitoring & alerts tab.
    private void ShowNotifications(ConnectionStore store)
    {
        var muted = store.Connections
            .Where(c => !store.NotifiesFor(c.Id))
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        ChkNotifications.IsEnabled = store.Connections.Count > 0;
        ChkNotifications.IsChecked = store.Connections.Count > 0 && muted.Count < store.Connections.Count;

        TxtMuted.Inlines.Clear();
        if (store.Connections.Count == 0)
        {
            TxtMuted.Inlines.Add(new Run("No saved connections yet."));
        }
        else if (muted.Count > 0)
        {
            TxtMuted.Inlines.Add(new Run(muted.Count == store.Connections.Count ? "Every connection is muted: " : "Muted: "));
            for (var i = 0; i < muted.Count; i++)
            {
                if (i > 0)
                    TxtMuted.Inlines.Add(new Run(", "));
                var id   = muted[i].Id;
                var link = new Hyperlink(new Run(muted[i].DisplayName)) { ToolTip = "Open its Monitoring & alerts tab" };
                link.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                System.Windows.Automation.AutomationProperties.SetName(link, $"{muted[i].DisplayName}: Monitoring & alerts tab");
                link.Click += (_, _) => _context.Open(SettingsLocation.Connection(id, ConnectionTab.Monitoring));
                TxtMuted.Inlines.Add(link);
            }
        }
        TxtMuted.Visibility = TxtMuted.Inlines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Off mutes every connection, on unmutes them all: there is no setting of its own (#131).
    private void ChkNotifications_Click(object sender, RoutedEventArgs e)
    {
        var notify = ChkNotifications.IsChecked == true;
        var store  = _context.Service.Load();
        try
        {
            foreach (var conn in store.Connections.Where(c => store.NotifiesFor(c.Id) != notify))
                _context.Service.SetNotificationsMuted(conn.Id, muted: !notify);
            SettingsContext.ShowStatus(TxtAlertsStatus,
                notify
                    ? "Saved: alerts on every connection raise a Windows notification."
                    : "Saved: every connection is muted. Its alerts are still listed on the Alerts page and posted to its chat channels.",
                success: true);
        }
        catch (Exception ex)
        {
            SettingsContext.ShowStatus(TxtAlertsStatus, $"Could not save the notification setting: {ex.Message}", success: false);
        }
        ShowNotifications(_context.Service.Load());
    }

    // ── Chat channels (#120) ──────────────────────────────────────────────────

    private void PopulateChatChannels(ConnectionStore store)
    {
        var now   = DateTime.Now;
        var cards = store.ChatChannels
            .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => ChatChannelCard.From(c, _context.Chat.Status(c.Id), _context.Chat.ConnectionsText(c), now))
            .ToList();

        ChannelCards.ItemsSource     = cards;
        ChannelCards.Visibility      = cards.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TxtNoChatChannels.Visibility = cards.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private void OnChatStatusChanged(Guid channelId) => PopulateChatChannels(_context.Service.Load());

    private void BtnAddChatChannel_Click(object sender, RoutedEventArgs e)
    {
        OpenChannelForm(null, _context.Service.Load());
        SettingsContext.ShowStatus(TxtChatStatus, string.Empty, success: null);
        TxtChatName.Focus();
    }

    private void EditChannel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ChatChannelCard card })
            return;
        var store = _context.Service.Load();
        if (store.ChatChannels.FirstOrDefault(c => c.Id == card.Id) is { } channel)
        {
            OpenChannelForm(channel, store);
            SettingsContext.ShowStatus(TxtChatStatus, string.Empty, success: null);
            TxtChatName.Focus();
        }
    }

    private void OpenChannelForm(ChatChannel? channel, ConnectionStore store)
    {
        _editingChannelId = channel?.Id;
        _savedChannelUrl  = channel?.WebhookUrl;
        _channelUrlShown  = channel is null;

        TxtChatFormTitle.Text        = channel is null ? "New channel" : $"Edit channel — {channel.Name}";
        TxtChatName.Text             = channel?.Name ?? string.Empty;
        TxtChatUrl.Text              = string.Empty;
        TxtChatUrlMasked.Text        = WebhookUrl.Mask(channel?.WebhookUrl);
        PnlChatUrlMasked.Visibility  = channel is null ? Visibility.Collapsed : Visibility.Visible;
        TxtChatUrl.Visibility        = channel is null ? Visibility.Visible : Visibility.Collapsed;
        CmbChatService.SelectedIndex = channel?.Service == ChatService.Teams ? 1 : 0;
        ChkChatEnabled.IsChecked     = channel?.Enabled ?? true;
        CmbChatPost.SelectedIndex    = channel?.CriticalOnly == true ? 1 : 0;
        ChkChatPostEnds.IsChecked    = channel?.PostEnds ?? true;
        LstChatConnections.ItemsSource = store.Connections
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new ChatConnectionItem(c.Id, c.DisplayName) { Chosen = channel?.ConnectionIds.Contains(c.Id) == true })
            .ToList();
        if (channel?.AllConnections == false)
            RadChatChosenConnections.IsChecked = true;
        else
            RadChatAllConnections.IsChecked = true;
        LstChatConnections.IsEnabled     = RadChatChosenConnections.IsChecked == true;
        BtnRemoveChatChannel.Visibility  = channel is null ? Visibility.Collapsed : Visibility.Visible;
        ShowChatFormMessage(null, null);
        ChatChannelForm.Visibility = Visibility.Visible;
        ChatChannelForm.BringIntoView();
    }

    private void CloseChannelForm()
    {
        ChatChannelForm.Visibility = Visibility.Collapsed;
        TxtChatUrl.Text   = string.Empty;
        _savedChannelUrl  = null;
        _editingChannelId = null;
    }

    private void BtnShowChatUrl_Click(object sender, RoutedEventArgs e)
    {
        _channelUrlShown = true;
        // Filled in code: the service the user picked stays as it is.
        _isFillingChatUrl = true;
        try { TxtChatUrl.Text = _savedChannelUrl ?? string.Empty; }
        finally { _isFillingChatUrl = false; }
        PnlChatUrlMasked.Visibility = Visibility.Collapsed;
        TxtChatUrl.Visibility       = Visibility.Visible;
        TxtChatUrl.Focus();
    }

    // Picks the service from the URL as it's typed or pasted; the user can still change it.
    private void TxtChatUrl_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isFillingChatUrl && WebhookUrl.Detect(TxtChatUrl.Text) is { } detected)
            CmbChatService.SelectedIndex = detected == ChatService.Teams ? 1 : 0;
    }

    private void BtnCancelChatChannel_Click(object sender, RoutedEventArgs e) => CloseChannelForm();

    private void BtnSaveChatChannel_Click(object sender, RoutedEventArgs e)
    {
        var store   = _context.Service.Load();
        var id      = _editingChannelId ?? Guid.NewGuid();
        var name    = TxtChatName.Text.Trim();
        var service = CmbChatService.SelectedIndex == 1 ? ChatService.Teams : ChatService.Slack;
        var url     = _channelUrlShown ? TxtChatUrl.Text.Trim() : _savedChannelUrl ?? string.Empty;

        if (name.Length == 0)
        {
            ShowChatFormMessage("Enter a name for the channel, e.g. DBA team (Teams).", success: false);
            TxtChatName.Focus();
            return;
        }
        if (store.ChatChannels.Any(c => c.Id != id && string.Equals(c.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)))
        {
            ShowChatFormMessage($"A channel named \"{name}\" already exists. Enter a different name.", success: false);
            TxtChatName.Focus();
            return;
        }

        var allConnections = RadChatAllConnections.IsChecked == true;
        var chosen = (LstChatConnections.ItemsSource as IEnumerable<ChatConnectionItem> ?? [])
            .Where(i => i.Chosen).Select(i => i.Id).ToList();
        if (!allConnections && chosen.Count == 0)
        {
            ShowChatFormMessage("Tick at least one connection, or choose All monitored connections.", success: false);
            return;
        }

        var check = WebhookUrl.Check(url, service, store.ChatChannels, id);
        if (!check.IsValid)
        {
            ShowChatFormMessage(check.Error, success: false);
            if (!_channelUrlShown)
                BtnShowChatUrl_Click(sender, e);
            TxtChatUrl.Focus();
            return;
        }

        var channel = new ChatChannel
        {
            Id             = id,
            Name           = name,
            Service        = service,
            WebhookUrl     = url,
            Enabled        = ChkChatEnabled.IsChecked == true,
            AllConnections = allConnections,
            ConnectionIds  = chosen,
            CriticalOnly   = CmbChatPost.SelectedIndex == 1,
            PostEnds       = ChkChatPostEnds.IsChecked == true,
        };

        try
        {
            _context.Service.SaveChatChannel(channel);
        }
        catch (Exception ex)
        {
            ShowChatFormMessage($"Could not save the channel: {ex.Message}", success: false);
            return;
        }

        var saved = _context.Service.Load();
        PopulateChatChannels(saved);
        if (saved.ChatChannels.FirstOrDefault(c => c.Id == id) is { } savedChannel)
            OpenChannelForm(savedChannel, saved);

        SettingsContext.ShowStatus(TxtChatStatus,
            $"Saved \"{name}\"" + (channel.Enabled ? "." : ", turned off.") +
            (check.Warning is { } warning ? $" {warning}" : " Send a test message to check it reaches the channel."),
            success: check.Warning is null ? true : null);
    }

    private void BtnRemoveChatChannel_Click(object sender, RoutedEventArgs e)
    {
        if (_editingChannelId is not { } id)
            return;
        var name = _context.Service.Load().ChatChannels.FirstOrDefault(c => c.Id == id)?.Name ?? TxtChatName.Text.Trim();

        var confirm = _context.Ask(
            $"Remove the chat channel \"{name}\"?\n\nSqlVitals stops posting to it. The webhook itself stays in Slack or Teams until you delete it there.",
            "Remove Chat Channel", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
            return;

        try
        {
            _context.Service.RemoveChatChannel(id);
        }
        catch (Exception ex)
        {
            SettingsContext.ShowStatus(TxtChatStatus, $"Could not remove the channel: {ex.Message}", success: false);
            return;
        }

        CloseChannelForm();
        PopulateChatChannels(_context.Service.Load());
        SettingsContext.ShowStatus(TxtChatStatus, $"Removed \"{name}\".", success: true);
    }

    private async void SendTest_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChatChannelCard card } button)
            return;
        var channel = _context.Chat.Channels.FirstOrDefault(c => c.Id == card.Id);
        if (channel is null)
        {
            SettingsContext.ShowStatus(TxtChatStatus, $"\"{card.Name}\" isn't saved any more.", success: false);
            return;
        }

        button.IsEnabled = false;
        SettingsContext.ShowStatus(TxtChatStatus, $"Sending a test message to \"{card.Name}\"…", success: null);
        try
        {
            var result = await _context.Chat.SendTestAsync(card.Id);
            SettingsContext.ShowStatus(TxtChatStatus, $"\"{card.Name}\": {result.Describe(channel.Service)}", success: result.IsSuccess);
        }
        catch (Exception ex)
        {
            SettingsContext.ShowStatus(TxtChatStatus, $"Could not send the test message: {ex.Message}", success: false);
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private void ShowChatFormMessage(string? message, bool? success)
    {
        if (message is null)
            TxtChatFormMessage.Visibility = Visibility.Collapsed;
        else
            SettingsContext.ShowStatus(TxtChatFormMessage, message, success);
    }

    private void ChatConnectionsChoice_Changed(object sender, RoutedEventArgs e)
    {
        LstChatConnections?.IsEnabled = RadChatChosenConnections.IsChecked == true;
    }

    /// <summary>A tick box under Connections in the channel form.</summary>
    public sealed class ChatConnectionItem(Guid id, string name)
    {
        public Guid   Id     { get; } = id;
        public string Name   { get; } = name;
        public bool   Chosen { get; set; }
    }
}

/// <summary>
/// A chat channel's card in Settings → Alerts &amp; chat (#131): never the URL, only its mask.
/// </summary>
public sealed record ChatChannelCard(
    Guid                  Id,
    string                Name,
    string                Detail,
    IReadOnlyList<string> Tags,
    string                Label,
    Brush                 Dot,
    Brush                 LabelTint,
    string                StatusText,
    Brush                 StatusBrush)
{
    public string AutomationName => $"{Name}, {Label.ToLower(CultureInfo.CurrentCulture)}, {StatusText}";
    public string TestName       => $"Send a test message to {Name}";
    public string EditName       => $"Edit {Name}";

    /// <param name="connections">Which connections it posts for, as the test message says it.</param>
    public static ChatChannelCard From(ChatChannel channel, ChannelStatus status, string connections, DateTime nowLocal)
    {
        // Off first: a paused channel isn't failing, it's not being used.
        var (label, level) = !channel.Enabled ? ("OFF", TileStatus.Off)
                           : status.IsFailing ? ("FAILING", TileStatus.Critical)
                           : status.LastPostedUtc is not null ? ("WORKING", TileStatus.Healthy)
                           : ("NOT USED YET", TileStatus.Off);

        var tags = new List<string>
        {
            connections.Length == 0 ? connections : char.ToUpper(connections[0], CultureInfo.CurrentCulture) + connections[1..],
            channel.CriticalOnly ? "Critical only" : "Warnings and critical",
        };
        if (!channel.PostEnds)
            tags.Add("No end messages");

        return new ChatChannelCard(
            channel.Id,
            channel.Name,
            $"{ChatServices.Name(channel.Service)} · {WebhookUrl.Mask(channel.WebhookUrl)}",
            tags,
            label,
            HealthBrushes.For(level),
            HealthBrushes.Tint(level),
            status.Text(nowLocal),
            status.IsFailing ? HealthBrushes.Red : HealthBrushes.Grey);
    }
}
