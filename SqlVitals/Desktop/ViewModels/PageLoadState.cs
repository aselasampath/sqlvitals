namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// Whether a page has read anything yet (#117), for what its empty grids and its verdict say
/// meanwhile, so an empty grid always means no rows (<c>Controls/LoadingPlaceholder</c>).
/// </summary>
public enum PageLoadState
{
    /// <summary>It has read, or tried to: an empty grid has no rows, and a failure is in the verdict.</summary>
    Ready,

    /// <summary>Its first read is running: "Loading…".</summary>
    Loading,

    /// <summary>Its first read was stopped from the loading bar: nothing was read.</summary>
    Stopped,
}
