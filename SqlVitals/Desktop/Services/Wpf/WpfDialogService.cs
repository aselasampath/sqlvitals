using System.Windows;
using SqlVitals.Desktop.Windows;
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

    private static MessageBoxResult Show(string message, string caption, MessageBoxButton buttons, MessageBoxImage icon,
                                         MessageBoxResult defaultResult = MessageBoxResult.None) =>
        OwnerWindow() is { } owner
            ? MessageBox.Show(owner, message, caption, buttons, icon, defaultResult)
            : MessageBox.Show(message, caption, buttons, icon, defaultResult);

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
