using System.Collections;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Scripting;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Stale Statistics (#184): the statistics with pending modifications, the script that updates the
/// ones selected, and the verdict on the one with the most of its table changed (#150).
/// </summary>
public sealed partial class StaleStatisticsViewModel(IIndexRepository repo, IDialogService dialogs, TimeProvider? time = null) : PageViewModel
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The statistics, as read: the most modified first.</summary>
    public GridRows<StaleStatistic> Statistics { get; } = [];

    /// <summary>The grid's current row; kept across a refresh by its database, schema, table and name.</summary>
    [ObservableProperty]
    private StaleStatistic? _selectedStatistic;

    /// <summary>
    /// Every row selected, as the view says: the script covers these, or all when none is. Kept
    /// across a refresh by their keys; the view selects them again when the view model sets it.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScriptText))]
    private IReadOnlyList<StaleStatistic> _selectedStatistics = [];

    /// <summary>The script reads every row (WITH FULLSCAN) rather than SQL Server's sample.</summary>
    [ObservableProperty]
    private bool _fullScan;

    /// <summary>The script button: for the rows selected, or all of them.</summary>
    public string ScriptText => SelectedStatistics.Count > 0
        ? $"Generate UPDATE STATISTICS Script ({SelectedStatistics.Count} selected)"
        : "Generate UPDATE STATISTICS Script (all)";

    /// <summary>A row to scroll into view: the verdict's Show it selected it.</summary>
    public event Action<object>? ShowRow;

    protected override async Task LoadAsync(CancellationToken cancel)
    {
        var stats = (await repo.GetStaleStatisticsAsync(cancel)).ToList();
        Verdict = StaleStatsVerdict.From(stats);

        // The grid clears its selection as the rows are replaced: note it first, by key.
        var current  = SelectedStatistic is { } row ? Key(row) : null;
        var selected = SelectedStatistics.Select(Key).ToHashSet();
        Statistics.Replace(stats);
        SelectedStatistic  = current is { } key ? stats.FirstOrDefault(s => Key(s) == key) : null;
        SelectedStatistics = stats.Where(s => selected.Contains(Key(s))).ToList();
        GenerateScriptCommand.NotifyCanExecuteChanged();
    }

    private static (string?, string?, string?, string?)? Key(StaleStatistic s) => (s.DatabaseName, s.SchemaName, s.TableName, s.StatisticsName);

    // The view model's choice of row: the grid selects it alone.
    private void Select(StaleStatistic? row)
    {
        SelectedStatistic  = row;
        SelectedStatistics = row is null ? [] : [row];
    }

    protected override void ShowLoadError(Exception exception) => ShowError("the statistics", exception);

    // The verdict's Show it: the most changed statistic, selected and in view.
    protected override void OnAction(VerdictAction action)
    {
        if (action.Key != StaleStatsVerdict.ShowWorst || StaleStatsVerdict.Worst(Statistics) is not { } worst)
            return;
        Select(worst);
        ShowRow?.Invoke(worst);
    }

    /// <summary>
    /// Shows the UPDATE STATISTICS script for the rows selected, or all of them, for review: nothing
    /// is run. <paramref name="shown"/> is the grid's rows in its sort order, so the script reads
    /// like what's on screen; null takes them as read.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanGenerateScript))]
    private void GenerateScript(IEnumerable? shown)
    {
        var selected   = SelectedStatistics.ToHashSet();
        var statistics = (shown?.OfType<StaleStatistic>() ?? Statistics)
            .Where(s => selected.Count == 0 || selected.Contains(s))
            .ToList();
        if (statistics.Count == 0)
            return;

        var sampling = FullScan ? StatisticsSampling.FullScan : StatisticsSampling.Default;
        var summary  = UpdateStatisticsScript.Summarize(statistics);
        var notice =
            $"Review before running. {summary.Statistics:N0} statistic(s) on {summary.Tables:N0} table(s) will be updated " +
            (sampling == StatisticsSampling.FullScan
                ? $"WITH FULLSCAN, reading about {summary.RowsInTables:N0} rows — run it off-peak."
                : "with default sampling.") +
            " Plans that use them recompile on their next run. SqlVitals does not run this script.";

        var now = _time.GetLocalNow().DateTime;
        dialogs.ShowSqlScript(new SqlScript(
            "Update Statistics",
            notice,
            UpdateStatisticsScript.Build(statistics, sampling, now),
            $"UpdateStatistics_{now:yyyyMMdd_HHmm}.sql"), modal: true);
    }

    private bool CanGenerateScript(IEnumerable? shown) => Statistics.Count > 0;
}
