namespace SqlVitals.Desktop.Services;

/// <summary>Where the details drawer is (#147).</summary>
public enum DrawerState
{
    Closed,

    /// <summary>Over the right of the page, which keeps its width.</summary>
    Floating,

    /// <summary>Beside the page, which gets narrower.</summary>
    Pinned,
}

/// <summary>
/// The details drawer's state (#147): closed, floating over the grid or pinned beside it, and its
/// width. Pure; <c>DrawerHost</c> lays it out. Whether it pins and its width are the user's
/// preferences, saved; whether it is open is not.
/// </summary>
public sealed class DrawerLayout
{
    public const double MinWidth     = 320;
    public const double MaxWidth     = 640;
    public const double DefaultWidth = 380;

    /// <param name="pinned">Whether it opens pinned, as last chosen.</param>
    /// <param name="width">Its width as last dragged; clamped to <see cref="MinWidth"/>–<see cref="MaxWidth"/>.</param>
    public DrawerLayout(bool pinned = false, double width = DefaultWidth)
    {
        Pinned = pinned;
        Width  = Clamp(width);
    }

    public DrawerState State { get; private set; } = DrawerState.Closed;

    /// <summary>It opens pinned rather than floating: the pin button's choice.</summary>
    public bool Pinned { get; private set; }

    public double Width { get; private set; }

    public bool IsOpen => State != DrawerState.Closed;

    /// <summary>Ctrl+J: opens it as last pinned or floating, or closes it.</summary>
    public void Toggle()
    {
        if (IsOpen) Close();
        else        Open();
    }

    /// <summary>Opens it as last pinned or floating; nothing when it is open.</summary>
    public void Open()
    {
        if (!IsOpen)
            State = Pinned ? DrawerState.Pinned : DrawerState.Floating;
    }

    public void Close() => State = DrawerState.Closed;

    /// <summary>Esc: closes it when it floats, as a pop-up would. True when it did.</summary>
    public bool Escape()
    {
        if (State != DrawerState.Floating)
            return false;
        Close();
        return true;
    }

    /// <summary>The pin button: floating to pinned and back. Remembered for the next time it opens.</summary>
    public void TogglePin()
    {
        Pinned = !Pinned;
        if (IsOpen)
            State = Pinned ? DrawerState.Pinned : DrawerState.Floating;
    }

    /// <summary>Its left edge dragged: the new width, kept within its limits.</summary>
    public double Resize(double width) => Width = Clamp(width);

    /// <summary>A width within the limits; the default for one that isn't a number.</summary>
    public static double Clamp(double width) =>
        double.IsFinite(width) ? Math.Clamp(width, MinWidth, MaxWidth) : DefaultWidth;
}

/// <summary>What the details drawer shows (#147): a title, and the page's content or what to select.</summary>
/// <param name="Body">The details; null shows <paramref name="Hint"/> instead.</param>
/// <param name="Hint">What to select for details: "Select a session to see its details."</param>
public sealed record DrawerContent(string Title, System.Windows.UIElement? Body, string Hint)
{
    /// <summary>A page that supplies nothing for the drawer.</summary>
    public static DrawerContent None { get; } =
        new("Details", null, "This page has no details to show here. On Processes, Query Store, Resource Queries, Query Regressions, SP Trace, " +
                             "File I/O, Index Health, Agent Jobs, Config Checks, Security Audit and Alerts, select a row to see its details.");
}

/// <summary>
/// A page that fills the details drawer (#147): the selected row's details, or what to select.
/// The drawer is only a host: a page supplies its content, as the AI assistant will (#93).
/// </summary>
public interface IDetailsSource
{
    /// <summary>What the drawer shows for the page now.</summary>
    DrawerContent Details { get; }

    /// <summary>
    /// The details changed. <c>true</c> when the user asked to see them (a click on a row), so a
    /// closed drawer opens; <c>false</c> for a selection moved by the keyboard or a refresh.
    /// </summary>
    event Action<bool>? DetailsChanged;
}
