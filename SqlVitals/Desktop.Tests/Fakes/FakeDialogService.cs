using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.QueryStore;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Tests.Fakes;

/// <summary>An <see cref="IDialogService"/> that answers as told and writes down what it was asked (#182).</summary>
internal sealed class FakeDialogService : IDialogService
{
    /// <summary>What <see cref="Confirm"/> answers.</summary>
    public bool ConfirmAnswer { get; set; }

    /// <summary>What <see cref="AskToSave"/> answers.</summary>
    public DialogAnswer SaveAnswer { get; set; } = DialogAnswer.Cancel;

    /// <summary>Each message and window, as "Warn: Copy Failed: …", "Script: Update Statistics (modal)".</summary>
    public List<string> Shown { get; } = [];

    public bool Confirm(string message, string caption)
    {
        Shown.Add($"Confirm: {caption}: {message}");
        return ConfirmAnswer;
    }

    public DialogAnswer AskToSave(string message, string caption)
    {
        Shown.Add($"AskToSave: {caption}: {message}");
        return SaveAnswer;
    }

    public void Inform(string message, string caption) => Shown.Add($"Inform: {caption}: {message}");

    public void Warn(string message, string caption) => Shown.Add($"Warn: {caption}: {message}");

    public void Error(string message, string caption) => Shown.Add($"Error: {caption}: {message}");

    public void ShowSqlScript(SqlScript script, bool modal) => Shown.Add($"Script: {script.Heading} ({(modal ? "modal" : "modeless")})");

    public void ShowQueryPlan(string? planXml, string? queryText, string? title = null) => Shown.Add($"Plan: {title ?? "(untitled)"}");

    public void ShowQueryStoreQuery(IWaitStatsRepository repo, long queryId, QueryStorePeriod period, QueryStoreStatus status) =>
        Shown.Add($"Query Store query {queryId}");
}
