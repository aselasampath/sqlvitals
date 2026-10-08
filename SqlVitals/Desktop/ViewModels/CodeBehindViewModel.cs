using SqlVitals.Desktop.Pages;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// The view model of a page whose code-behind still reads (#183): loading calls the page's
/// <see cref="ICodeBehindPage.RefreshAsync"/>, as the window did before. The page reports its own
/// errors in its verdict, and runs its own timer. Each wave of #180 moves a page's reading into its
/// view model, which then derives from <see cref="PageViewModel"/> instead.
/// </summary>
public abstract class CodeBehindViewModel : PageViewModel
{
    private ICodeBehindPage? _page;

    /// <summary>The page whose code-behind reads; the page factory sets it.</summary>
    internal void LoadThrough(ICodeBehindPage page) => _page = page;

    // The code-behind doesn't take a token: its repository is bound to the page's lifetime instead
    // (#167), so leaving the page cancels what it reads, and the base class keeps it off the screen.
    protected override Task LoadAsync(CancellationToken cancel) => _page?.RefreshAsync() ?? Task.CompletedTask;

    // The page has already put it in its verdict, saying what it couldn't read.
    protected override void ShowLoadError(Exception exception) { }
}
