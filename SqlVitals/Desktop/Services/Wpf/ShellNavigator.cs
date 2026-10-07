using SqlVitals.Engine.Alerts;

namespace SqlVitals.Desktop.Services.Wpf;

/// <summary>
/// <see cref="INavigator"/> through the main window's navigator (#139). Before the window opens,
/// or in a test without one, it does nothing.
/// </summary>
/// <param name="shell">The main window, while there is one.</param>
internal sealed class ShellNavigator(Func<MainWindow?> shell) : INavigator
{
    public Task GoToAsync(string tag, Guid? serverId = null)
    {
        Check(tag);
        return shell() is { } main ? main.OpenPageAsync(tag, serverId) : Task.CompletedTask;
    }

    public Task GoToAsync(string tag, FocusFilter filter, string fromPage)
    {
        Check(tag);
        return shell() is { } main ? main.OpenLinkAsync(tag, filter, fromPage) : Task.CompletedTask;
    }

    public Task InvestigateAsync(Alert alert, string source) =>
        shell() is { } main ? main.OpenEvidenceAsync(alert, source) : Task.CompletedTask;

    public Task BackAsync() => shell() is { } main ? main.GoAsync(back: true) : Task.CompletedTask;

    public Task ForwardAsync() => shell() is { } main ? main.GoAsync(back: false) : Task.CompletedTask;

    // The window opens Live Metrics for a tag it doesn't know; a link to one is a bug, said at once,
    // with or without a window.
    private static void Check(string tag)
    {
        if (!PageCatalog.Types.ContainsKey(tag))
            throw new ArgumentException($"No page has the tag \"{tag}\" (PageCatalog).", nameof(tag));
    }
}
