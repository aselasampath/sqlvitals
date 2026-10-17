using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.ChatNotifications;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// Alerts &amp; chat's form for a new or saved chat channel (#120, #170). The saved webhook URL and
/// heartbeat ping URL are secrets: neither is put in a bound property, so in no control (or UI
/// Automation client), until Show is pressed; until then the form saves them as they were.
/// </summary>
public sealed partial class ChatChannelForm : ObservableObject
{
    // The schedule choices, in order: Off, every 1…12 hours, Daily at (with a time).
    private static readonly int DailyIndex = 1 + HeartbeatSchedule.AllowedHours.Count;
    private static readonly IReadOnlyList<TimeSpan> HeartbeatTimes =
        Enumerable.Range(0, 48).Select(i => TimeSpan.FromMinutes(30 * i)).ToList();

    private readonly IClipboard _clipboard;
    private readonly string? _savedUrl;
    private readonly string? _savedPingUrl;

    // Set while the URL is filled in code, so the service the user picked stays as it is.
    private bool _fillingUrl;

    /// <param name="channel">The saved channel; null for a new one.</param>
    /// <param name="connections">The saved connections, for its tick boxes.</param>
    internal ChatChannelForm(ChatChannel? channel, IEnumerable<ConnectionSettings> connections, IClipboard clipboard)
    {
        _clipboard    = clipboard;
        ChannelId     = channel?.Id;
        _savedUrl     = channel?.WebhookUrl;
        _savedPingUrl = channel?.HeartbeatPingUrl;

        Title          = channel is null ? "New channel" : $"Edit channel — {channel.Name}";
        _name          = channel?.Name ?? string.Empty;
        _isUrlShown    = channel is null;
        MaskedUrl      = WebhookUrl.Mask(channel?.WebhookUrl);
        _service       = channel?.Service ?? ChatService.Slack;
        _enabled       = channel?.Enabled ?? true;
        _criticalOnly  = channel?.CriticalOnly == true;
        _postEnds      = channel?.PostEnds ?? true;
        _allConnections = channel?.AllConnections != false;
        Connections = connections
            .OrderBy(c => c.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Select(c => new ChatConnectionItem(c.Id, c.DisplayName) { Chosen = channel?.ConnectionIds.Contains(c.Id) == true })
            .ToList();

        var schedule = channel?.Heartbeat ?? HeartbeatSchedule.Off;
        _heartbeatIndex = !schedule.IsOn                         ? 0
                        : schedule.Every == HeartbeatEvery.Daily ? DailyIndex
                        : 1 + HeartbeatSchedule.AllowedHours.ToList().IndexOf(schedule.Hours);
        var at = HeartbeatTimes.ToList().IndexOf(schedule.At);
        _heartbeatAtIndex = at >= 0 ? at : HeartbeatTimes.ToList().IndexOf(HeartbeatSchedule.DefaultAt);
        _isPingUrlShown   = string.IsNullOrWhiteSpace(_savedPingUrl);
        MaskedPingUrl     = $"{WebhookUrl.Mask(_savedPingUrl)} · press Show above to see or change it";
    }

    /// <summary>A field to put the keyboard focus in: "Name", "Url" or "PingUrl".</summary>
    public event Action<string>? FocusRequested;

    /// <summary>The channel being edited; null for a new one.</summary>
    public Guid? ChannelId { get; }

    public bool CanRemove => ChannelId is not null;

    public string Title { get; }

    [ObservableProperty]
    private string _name;

    /// <summary>The webhook URL as typed; empty for a saved one until Show.</summary>
    [ObservableProperty]
    private string _url = string.Empty;

    [ObservableProperty]
    private bool _isUrlShown;

    /// <summary>"hooks.slack.com/…/abcd": the saved URL, masked, until Show.</summary>
    public string MaskedUrl { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeartbeatSteps))]
    private ChatService _service;

    public IReadOnlyList<Choice<ChatService>> ServiceChoices { get; } =
        [new(ChatService.Slack, "Slack"), new(ChatService.Teams, "Microsoft Teams")];

    /// <summary>Posts for every monitored connection; otherwise only the ones ticked.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OnlyChosen))]
    private bool _allConnections;

    public bool OnlyChosen
    {
        get => !AllConnections;
        set => AllConnections = !value;
    }

    public IReadOnlyList<ChatConnectionItem> Connections { get; }

    [ObservableProperty]
    private bool _criticalOnly;

    public IReadOnlyList<Choice<bool>> PostChoices { get; } =
        [new(false, "Warnings and critical"), new(true, "Critical only")];

    [ObservableProperty]
    private bool _postEnds;

    [ObservableProperty]
    private bool _enabled;

    /// <summary>Off, Every hour … Every 12 hours, Daily at.</summary>
    public IReadOnlyList<string> HeartbeatChoices { get; } =
        ["Off", .. HeartbeatSchedule.AllowedHours.Select(h => h == 1 ? "Every hour" : $"Every {h} hours"), "Daily at"];

    public IReadOnlyList<string> HeartbeatAtChoices { get; } = HeartbeatTimes.Select(HeartbeatSchedule.Clock).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsHeartbeatAt), nameof(HeartbeatSteps))]
    private int _heartbeatIndex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeartbeatSteps))]
    private int _heartbeatAtIndex;

    /// <summary>The time box, only for Daily at.</summary>
    public bool ShowsHeartbeatAt => HeartbeatIndex == DailyIndex;

    /// <summary>The ping URL as typed; empty for a saved one until Show.</summary>
    [ObservableProperty]
    private string _pingUrl = string.Empty;

    [ObservableProperty]
    private bool _isPingUrlShown;

    public string MaskedPingUrl { get; }

    /// <summary>Show how to get an alert when a heartbeat is missing: view only.</summary>
    [ObservableProperty]
    private bool _stepsShown;

    /// <summary>The steps for the service and the schedule chosen now.</summary>
    public string HeartbeatSteps => HeartbeatSetup.Steps(Service, ChosenHeartbeat(), Environment.MachineName);

    /// <summary>What Save said, under the form.</summary>
    public StatusLine Message { get; } = new();

    /// <summary>The heartbeat chosen in the form.</summary>
    public HeartbeatSchedule ChosenHeartbeat() => HeartbeatIndex switch
    {
        <= 0                     => HeartbeatSchedule.Off,
        var i when i == DailyIndex => HeartbeatSchedule.DailyAt(HeartbeatTimes[Math.Max(0, HeartbeatAtIndex)]),
        var i                    => HeartbeatSchedule.EveryHours(HeartbeatSchedule.AllowedHours[i - 1]),
    };

    /// <summary>The webhook URL to save: as typed once shown, as saved before.</summary>
    internal string UrlToSave => IsUrlShown ? Url.Trim() : _savedUrl ?? string.Empty;

    internal string PingUrlToSave => IsPingUrlShown ? PingUrl.Trim() : _savedPingUrl ?? string.Empty;

    // Picks the service from the URL as it's typed or pasted; the user can still change it.
    partial void OnUrlChanged(string value)
    {
        if (!_fillingUrl && WebhookUrl.Detect(value) is { } detected)
            Service = detected;
    }

    /// <summary>Show: the saved URL, and the ping URL with it, in their boxes to check or change.</summary>
    [RelayCommand]
    public void ShowUrl()
    {
        if (!IsUrlShown)
        {
            _fillingUrl = true;
            try { Url = _savedUrl ?? string.Empty; }
            finally { _fillingUrl = false; }
            IsUrlShown = true;
        }
        if (!IsPingUrlShown)
        {
            PingUrl = _savedPingUrl ?? string.Empty;
            IsPingUrlShown = true;
        }
        Focus("Url");
    }

    [RelayCommand]
    private void CopySteps()
    {
        _clipboard.SetText(HeartbeatSteps);
        Message.Show("Copied the heartbeat setup steps.", success: true);
    }

    internal void Focus(string field) => FocusRequested?.Invoke(field);
}
