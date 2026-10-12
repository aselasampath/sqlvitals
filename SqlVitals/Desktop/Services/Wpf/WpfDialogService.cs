using System.Windows;
using SqlVitals.Desktop.Windows;
using SqlVitals.Engine.Errors;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.QueryStore;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Services.Wpf;

/// <summary>
/// <see cref="IDialogService"/> with message boxes and the app's windows, each owned by the window
/// in front (<see cref="OwnerWindow"/>), so it stays over it and closes with it.
/// </summary>
internal sealed class WpfDialogService : IDialogService
{
    public bool Confirm(string message, string caption) =>
        Show(message, caption, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public DialogAnswer AskToSave(string message, string caption) =>
        Show(message, caption, MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel) switch
        {
            MessageBoxResult.Yes => DialogAnswer.Yes,
            MessageBoxResult.No  => DialogAnswer.No,
            _                    => DialogAnswer.Cancel,
        };

    public void Inform(string message, string caption) => Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Warn(string message, string caption) => Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);

    public void Error(string message, string caption) => Show(message, caption, MessageBoxButton.OK, MessageBoxImage.Error);

    public void ShowSqlScript(SqlScript script, bool modal)
    {
        var window = new SqlScriptWindow(script.Heading, script.Notice, script.Text, script.FileName) { Owner = OwnerWindow() };
        if (modal)
            window.ShowDialog();
        else
            window.Show();
    }

    public void ShowQueryPlan(string? planXml, string? queryText, string? title = null)
    {
        var window = new QueryExecutionPlanWindow(planXml, queryText) { Owner = OwnerWindow() };
        if (title is not null)
            window.Title = title;
        window.Show();
    }

    public void ShowQueryStoreQuery(IWaitStatsRepository repo, long queryId, QueryStorePeriod period, QueryStoreStatus status) =>
        new QueryStoreQueryWindow(repo, queryId, period, status) { Owner = OwnerWindow() }.Show();

    // A task dialog (WinForms, which the app has for its tray icon): two worded choices and
    // Don't ask again, which a message box can't do.
    public WatchAnswer? AskWhereToWatch(string serverName, bool serviceRunning)
    {
        var service = new System.Windows.Forms.TaskDialogCommandLinkButton("Watch with the collector service",
            "Keeps watching, alerting and posting to chat when SqlVitals is closed or you sign out" +
            (serviceRunning ? "." : ". The service is stopped: it will be started."));
        var app = new System.Windows.Forms.TaskDialogCommandLinkButton("Watch in SqlVitals",
            "While SqlVitals runs, in the window or the notification area, as before.");
        var page = new System.Windows.Forms.TaskDialogPage
        {
            Caption      = "Start monitoring",
            Heading      = $"Where should {serverName} be watched?",
            Text         = "The SqlVitals Collector service is installed on this PC. You can change this later in Settings → Collector service.",
            Buttons      = { service, app, System.Windows.Forms.TaskDialogButton.Cancel },
            Verification = new System.Windows.Forms.TaskDialogVerificationCheckBox("Don't ask again"),
            Icon         = System.Windows.Forms.TaskDialogIcon.Information,
            AllowCancel  = true,
        };
        var owner  = OwnerWindow() is { } window ? new System.Windows.Interop.WindowInteropHelper(window).Handle : IntPtr.Zero;
        var picked = owner == IntPtr.Zero ? System.Windows.Forms.TaskDialog.ShowDialog(page)
                                          : System.Windows.Forms.TaskDialog.ShowDialog(owner, page);
        if (picked == service)
            return new WatchAnswer(WatchChoice.Service, page.Verification.Checked);
        if (picked == app)
            return new WatchAnswer(WatchChoice.App, page.Verification.Checked);
        return null;
    }

    // Three worded choices, the recommended one first; the window closed keeps it as it was.
    public QueryTextStorage? AskQueryTextStorage()
    {
        var without = new System.Windows.Forms.TaskDialogCommandLinkButton("Without literals (recommended)",
            "Keeps each query's shape with its values replaced by ?, so names, emails or passwords " +
            "in a WHERE clause aren't saved.");
        var full = new System.Windows.Forms.TaskDialogCommandLinkButton("Full, as now",
            "Keeps each query exactly as SQL Server ran it, values and all.");
        var off = new System.Windows.Forms.TaskDialogCommandLinkButton("Off",
            "Keeps no text, only each query's hash. CPU, reads and executions are kept either way.");
        var page = new System.Windows.Forms.TaskDialogPage
        {
            Caption     = "Monitoring history",
            Heading     = "How should query text be kept in the history?",
            Text        = "SqlVitals saves the text of the busiest queries in its history file, which isn't encrypted. " +
                          "Text already saved stays until you press Clear stored query text in Settings → Monitoring & history, " +
                          "where you can also change this later.",
            Buttons     = { without, full, off },
            Icon        = System.Windows.Forms.TaskDialogIcon.Information,
            AllowCancel = true,
        };
        var owner  = OwnerWindow() is { } window ? new System.Windows.Interop.WindowInteropHelper(window).Handle : IntPtr.Zero;
        var picked = owner == IntPtr.Zero ? System.Windows.Forms.TaskDialog.ShowDialog(page)
                                          : System.Windows.Forms.TaskDialog.ShowDialog(owner, page);
        return picked == without ? QueryTextStorage.WithoutLiterals
             : picked == full    ? QueryTextStorage.Full
             : picked == off     ? QueryTextStorage.Off
             : null;
    }

    // Every message box goes through here: an error's text may echo a secret back (#175).
    private static MessageBoxResult Show(string message, string caption, MessageBoxButton buttons, MessageBoxImage icon,
                                         MessageBoxResult defaultResult = MessageBoxResult.None)
    {
        message = Secrets.Redact(message);
        return OwnerWindow() is { } owner
            ? MessageBox.Show(owner, message, caption, buttons, icon, defaultResult)
            : MessageBox.Show(message, caption, buttons, icon, defaultResult);
    }

    /// <summary>
    /// The window the user is in: the active one, else the main window while it is shown. Null
    /// while the app is in the tray, or before its window opens: a message box then stands alone.
    /// </summary>
    internal static Window? OwnerWindow()
    {
        if (Application.Current is not { } app)
            return null;
        return app.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
            ?? (app.MainWindow is { IsVisible: true } main ? main : null);
    }
}
