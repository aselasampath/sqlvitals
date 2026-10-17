using CommunityToolkit.Mvvm.ComponentModel;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.ViewModels.Shell;

/// <summary>
/// The sidebar (#142, #191): pinned, collapsed or hidden as <see cref="SidebarLayout"/> decides, the
/// hidden one slid in over the page, the open group, and what it shows: the open server's header,
/// the page on screen, the active alerts' count and the pages the active server hasn't got.
/// <c>WorkspaceShell</c> draws it; the pointer resting, the animation and the keyboard focus are its own.
/// </summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    private SidebarLayout _layout = new();

    /// <summary>The state on screen.</summary>
    public SidebarMode Shown => _layout.Shown;

    /// <summary>The state the user chose, which <see cref="Shown"/> is unless the window is narrow or in focus mode.</summary>
    public SidebarMode Saved => _layout.Saved;

    /// <summary>The user chose a state (the pin button, Ctrl+B, ☰): it is saved.</summary>
    public event Action<SidebarMode>? ModeChosen;

    /// <summary>A page was picked, in any of the three states.</summary>
    public event Action<string>? PageRequested;

    /// <summary>Start or Stop in the server header.</summary>
    public event Action? MonitorToggleRequested;

    // ── The state ────────────────────────────────────────────────────────────

    /// <summary>The saved state at start-up, at the window's width.</summary>
    public void Restore(SidebarMode saved, double windowWidth)
    {
        var inFocus = _layout.InFocus;
        CloseSlideOver(keyboard: false);
        _layout = new SidebarLayout(saved, windowWidth) { InFocus = inFocus };
        ModeChanged();
    }

    /// <summary>Focus mode (#148): hidden, whatever was chosen; off, the chosen state again. Not saved.</summary>
    public bool InFocus
    {
        get => _layout.InFocus;
        set
        {
            if (_layout.InFocus == value)
                return;
            CloseSlideOver(keyboard: false);
            _layout.InFocus = value;
            OnPropertyChanged();
            ModeChanged();
        }
    }

    /// <summary>The window was resized: below 1280 px a pinned sidebar shows collapsed.</summary>
    public void SetWindowWidth(double width)
    {
        if (!_layout.SetWindowWidth(width))
            return;
        CloseSlideOver(keyboard: false);
        ModeChanged();
    }

    /// <summary>Ctrl+B: Pinned → Collapsed → Hidden → Pinned. In focus mode it slides the menu in over the page, and out.</summary>
    public void Cycle()
    {
        if (InFocus)
        {
            if (IsSlidOver) CloseSlideOver(keyboard: true);
            else            SlideIn(keyboard: true);
            return;
        }
        Choose(SidebarLayout.Next(Shown));
    }

    /// <summary>
    /// The top bar's ☰: the same as Ctrl+B, except that a hidden sidebar slides in over the page
    /// (Ctrl+B, or its pin, pins it).
    /// </summary>
    public void ToggleFromBar()
    {
        if (Shown != SidebarMode.Hidden)
            Cycle();
        else if (IsSlidOver)
            CloseSlideOver(keyboard: true);
        else
            SlideIn(keyboard: true);
    }

    /// <summary>A state picked by the user, which is saved.</summary>
    public void Choose(SidebarMode mode)
    {
        CloseSlideOver(keyboard: false);
        _layout.Choose(mode);
        ModeChanged();
        ModeChosen?.Invoke(mode);
    }

    private void ModeChanged()
    {
        OnPropertyChanged(nameof(Saved));
        OnPropertyChanged(nameof(Shown));
    }

    // ── Hidden: slid in over the page ────────────────────────────────────────

    /// <summary>The hidden sidebar is slid in over the page.</summary>
    [ObservableProperty]
    private bool _isSlidOver;

    /// <summary>
    /// Whether the last slide in or out came from ☰, Ctrl+B or Esc, where the keyboard goes with
    /// it, rather than from the pointer, a pick, or the state changing.
    /// </summary>
    public bool SlideByKeyboard { get; private set; }

    /// <summary>Slides the hidden sidebar in over the page; nothing unless it is hidden.</summary>
    /// <param name="keyboard">From ☰ or Ctrl+B: the keyboard goes into it.</param>
    public void SlideIn(bool keyboard)
    {
        if (Shown != SidebarMode.Hidden || IsSlidOver)
            return;
        SlideByKeyboard = keyboard;
        IsSlidOver = true;
    }

    /// <summary>Slides the hidden sidebar out again, if it is in.</summary>
    /// <param name="keyboard">From ☰, Ctrl+B or Esc: the keyboard goes back to ☰.</param>
    public void CloseSlideOver(bool keyboard)
    {
        if (!IsSlidOver)
            return;
        SlideByKeyboard = keyboard;
        IsSlidOver = false;
    }

    // ── What it shows ────────────────────────────────────────────────────────

    /// <summary>The app's version under its name.</summary>
    [ObservableProperty]
    private string _version = string.Empty;

    /// <summary>The server whose pages are shown, as its selector row; null for none, which hides the groups.</summary>
    public ConnectionSelectorItem? Server { get; private set; }

    /// <summary>Shows a server's header, or none; again for the same server when its health changed.</summary>
    public void ShowServer(ConnectionSelectorItem? server)
    {
        Server = server;
        OnPropertyChanged(nameof(Server));
    }

    private string? _current;

    /// <summary>The page on screen, highlighted; its group opens.</summary>
    public string? Current
    {
        get => _current;
        set
        {
            _current = value;
            OnPropertyChanged();
            if (value is not null && NavMenu.GroupOf(value) is { } group)
                OpenGroup = group.Id;
        }
    }

    /// <summary>The pinned sidebar's open group: one at a time; null for none.</summary>
    [ObservableProperty]
    private string? _openGroup;

    /// <summary>The active alerts of every monitored connection, on Alerts' button, and on ☰ while hidden.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AlertsToolTip))]
    private int _alertCount;

    /// <summary>Alerts' tooltip: what it is, or how many are active.</summary>
    public string AlertsToolTip => AlertCount switch
    {
        0     => NavMenu.AlertsToolTip,
        1     => "1 active alert",
        var n => $"{n} active alerts",
    };

    /// <summary>
    /// The pages the open server hasn't got, with why: Azure SQL Database's (#141). Their buttons are
    /// disabled with the reason as their tooltip.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyDictionary<string, string> _unavailable = new Dictionary<string, string>();

    // ── What it asks for ─────────────────────────────────────────────────────

    /// <summary>A page's button, in any state.</summary>
    public void OpenPage(string tag) => PageRequested?.Invoke(tag);

    /// <summary>Start or Stop in the server header.</summary>
    public void ToggleMonitoring() => MonitorToggleRequested?.Invoke();
}
