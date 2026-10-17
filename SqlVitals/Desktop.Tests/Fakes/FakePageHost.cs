using SqlVitals.Desktop.Pages;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels;

namespace SqlVitals.Desktop.Tests.Fakes;

/// <summary>
/// The window's side of the shell (#191) for tests without one: it writes down what it was asked
/// to show, keep and put back, and answers for each page with a context that says what kind it is.
/// </summary>
internal sealed class FakePageHost : IPageHost
{
    /// <summary>The view model of each page shown, in order.</summary>
    public List<PageViewModel> Shown { get; } = [];

    /// <summary>Each call, as "Keep Processes", "Restore Processes", "Scroll Processes".</summary>
    public List<string> Calls { get; } = [];

    /// <summary>The focus each page was shown with, as evidence; null for none.</summary>
    public List<PageFocus?> Focus { get; } = [];

    /// <summary>What every page says it is; a snapshot unless a test says otherwise.</summary>
    public FakePageContext Context { get; set; } = new();

    public ShownPage Show(PageInstance page, PageFocus? focus)
    {
        Shown.Add(page.ViewModel);
        Focus.Add(focus);
        return new ShownPage(Context, null);
    }

    public void KeepViewState(Route route) => Calls.Add($"Keep {route.Tag}");

    public void RestoreViewState(Route route, bool scrolling) => Calls.Add($"{(scrolling ? "Scroll" : "Restore")} {route.Tag}");
}

/// <summary>What a page tells the top bar about itself, as a test sets it.</summary>
internal sealed class FakePageContext : IPageContext
{
    public PageKind Kind { get; set; } = PageKind.Snapshot;

    public bool CanPause { get; set; }

    public bool IsPaused { get; private set; }

    public bool UsesDatabase { get; set; }

    public void SetPaused(bool paused) => IsPaused = paused;
}
