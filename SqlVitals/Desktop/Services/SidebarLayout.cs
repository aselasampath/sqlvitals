namespace SqlVitals.Desktop.Services;

/// <summary>How the sidebar takes up the window (#142).</summary>
public enum SidebarMode
{
    /// <summary>232 px: labels, and the open group's pages.</summary>
    Pinned,

    /// <summary>56 px: an icon per global page and per group; a group's pages in a fly-out.</summary>
    Collapsed,

    /// <summary>0 px: the ☰ button, or pointing at the window's left edge, slides it in over the page.</summary>
    Hidden,
}

/// <summary>
/// Which way the sidebar is shown (#142): the state the user chose, saved in settings, and the
/// state on screen, which is collapsed instead of pinned while the window is narrower than
/// <see cref="NarrowBelow"/>, without the saved one changing. Ctrl+B and the top bar's ☰ move what
/// is on screen on to the next state; choosing pinned in a narrow window pins it until the window
/// next crosses that width.
/// </summary>
public sealed class SidebarLayout
{
    public const double PinnedWidth    = 232;
    public const double CollapsedWidth = 56;

    /// <summary>A window narrower than this shows a pinned sidebar collapsed.</summary>
    public const double NarrowBelow = 1280;

    // Pinned by the user while the window was narrow: shown pinned until it crosses the width.
    private bool _pinnedWhileNarrow;

    public SidebarLayout(SidebarMode saved = SidebarMode.Pinned, double windowWidth = double.PositiveInfinity)
    {
        Saved    = saved;
        IsNarrow = windowWidth < NarrowBelow;
    }

    /// <summary>The state the user chose, saved in settings and restored at start-up.</summary>
    public SidebarMode Saved { get; private set; }

    /// <summary>The window is narrower than <see cref="NarrowBelow"/>.</summary>
    public bool IsNarrow { get; private set; }

    /// <summary>
    /// Focus mode (#148): the sidebar is hidden whatever was chosen, which is kept to come back to.
    /// </summary>
    public bool InFocus { get; set; }

    /// <summary>The state on screen.</summary>
    public SidebarMode Shown =>
        InFocus ? SidebarMode.Hidden
        : Saved == SidebarMode.Pinned && IsNarrow && !_pinnedWhileNarrow ? SidebarMode.Collapsed : Saved;

    /// <summary>The sidebar's column: 232, 56 or 0 px.</summary>
    public double Width => WidthOf(Shown);

    public static double WidthOf(SidebarMode mode) => mode switch
    {
        SidebarMode.Pinned    => PinnedWidth,
        SidebarMode.Collapsed => CollapsedWidth,
        _                     => 0,
    };

    /// <summary>Ctrl+B or ☰: Pinned → Collapsed → Hidden → Pinned, from the state on screen.</summary>
    public void Cycle() => Choose(Next(Shown));

    /// <summary>The state after this one, as Ctrl+B goes.</summary>
    public static SidebarMode Next(SidebarMode mode) => mode switch
    {
        SidebarMode.Pinned    => SidebarMode.Collapsed,
        SidebarMode.Collapsed => SidebarMode.Hidden,
        _                     => SidebarMode.Pinned,
    };

    /// <summary>
    /// The user picked a state: the sidebar's pin button, or Ctrl+B. It is saved, and shown, even
    /// pinned in a narrow window.
    /// </summary>
    public void Choose(SidebarMode mode)
    {
        Saved = mode;
        _pinnedWhileNarrow = mode == SidebarMode.Pinned && IsNarrow;
    }

    /// <summary>
    /// The window was resized. Crossing <see cref="NarrowBelow"/> shows the saved state again,
    /// collapsed if it is pinned and the window is now narrow. True when that changed anything.
    /// </summary>
    public bool SetWindowWidth(double width)
    {
        var narrow = width < NarrowBelow;
        if (narrow == IsNarrow)
            return false;
        IsNarrow = narrow;
        _pinnedWhileNarrow = false;
        return true;
    }
}
