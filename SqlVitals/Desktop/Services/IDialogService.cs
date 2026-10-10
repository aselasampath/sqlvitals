using System.Diagnostics.CodeAnalysis;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.QueryStore;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Messages, questions and the app's own windows (#182), so a view model can ask for them without
/// WPF. The app's implementation (<see cref="Wpf.WpfDialogService"/>) shows them over the window in
/// front; tests answer with a fake.
/// </summary>
public interface IDialogService
{
    /// <summary>A yes-or-no question about something that can't be undone; true for Yes. No is the default.</summary>
    bool Confirm(string message, string caption);

    /// <summary>Unsaved changes: Yes saves, No discards, Cancel stays. Cancel is the default.</summary>
    DialogAnswer AskToSave(string message, string caption);

    /// <summary>Something went as asked: "Plan saved".</summary>
    void Inform(string message, string caption);

    /// <summary>Something the user can put right: a missing path, a busy clipboard.</summary>
    void Warn(string message, string caption);

    /// <summary>Something failed: a file that couldn't be written.</summary>
    [SuppressMessage("Naming", "CA1716", Justification = "Only C# calls it; Error is a keyword in Visual Basic")]
    void Error(string message, string caption);

    /// <summary>A generated script to review, copy or save; SqlVitals never runs it.</summary>
    /// <param name="modal">True keeps the user in the window until it is closed, as the pages' fix scripts do.</param>
    void ShowSqlScript(SqlScript script, bool modal);

    /// <summary>An execution plan, drawn, with its XML; <paramref name="planXml"/> null when there is none.</summary>
    /// <param name="title">The window's title; null for the window's own.</param>
    void ShowQueryPlan(string? planXml, string? queryText, string? title = null);

    /// <summary>
    /// Where to watch a server whose Start was pressed, with the collector service installed
    /// (#172): with the service, or in SqlVitals. Null when cancelled: nothing starts.
    /// </summary>
    WatchAnswer? AskWhereToWatch(string serverName, bool serviceRunning);

    /// <summary>One Query Store query (#111): its text, a metric per interval per plan, and its plans.</summary>
    void ShowQueryStoreQuery(IWaitStatsRepository repo, long queryId, QueryStorePeriod period, QueryStoreStatus status);
}

/// <summary>What <see cref="IDialogService.AskToSave"/> was answered.</summary>
public enum DialogAnswer
{
    Yes,
    No,
    Cancel,
}

/// <summary>A script for <see cref="IDialogService.ShowSqlScript"/>.</summary>
/// <param name="Heading">The window's title and heading: "Update Statistics".</param>
/// <param name="Notice">What to check before running it.</param>
/// <param name="Text">The script.</param>
/// <param name="FileName">What Save suggests: "UpdateStatistics_20261006_1405.sql".</param>
public sealed record SqlScript(string Heading, string Notice, string Text, string FileName);
