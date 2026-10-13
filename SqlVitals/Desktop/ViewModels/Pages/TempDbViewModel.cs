using CommunityToolkit.Mvvm.ComponentModel;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// TempDB Pressure (#184): the counters as cards, the files' used and free space, and the sessions
/// holding the most, with the verdict on who holds TempDB and how much room is left (#150).
/// </summary>
public sealed partial class TempDbViewModel(ITempDbRepository repo) : PageViewModel
{
    /// <summary>The files, for the used vs free chart; empty until read.</summary>
    [ObservableProperty]
    private IReadOnlyList<TempDbFile> _files = [];

    /// <summary>One card a counter: "12,288" over "Version Store Size (KB)", red for a version store in use, amber for little room left.</summary>
    [ObservableProperty]
    private IReadOnlyList<KpiCard> _counters = [];

    /// <summary>Folded, the counters say it in one line (#150).</summary>
    [ObservableProperty]
    private string? _countersSummary;

    /// <summary>Folded, the chart says it in one line (#150).</summary>
    [ObservableProperty]
    private string? _filesSummary;

    /// <summary>The selected session; kept across a refresh by its id.</summary>
    [ObservableProperty]
    private TempDbSession? _selectedSession;

    /// <summary>The sessions, the most allocated first.</summary>
    public GridRows<TempDbSession> Sessions { get; } = [];

    /// <summary>A row to scroll into view: the verdict's Show the session selected it.</summary>
    public event Action<object>? ShowRow;

    protected override async Task LoadAsync(CancellationToken cancel)
    {
        var (files, sessions, counters) = await repo.GetTempDbPressureAsync(cancel);
        Show(files.ToList(), sessions.ToList(), counters.ToList());
    }

    private void Show(List<TempDbFile> files, List<TempDbSession> sessions, List<TempDbCounter> counters)
    {
        Verdict = TempDbVerdict.From(files, sessions, counters);

        CountersSummary = counters.Count == 0 ? "No TempDB counters were read"
            : string.Join(" · ", counters.Take(3).Select(c => $"{c.CounterName.Trim()} {c.CounterValue:N0}"));
        FilesSummary = files.Count == 0 ? "No TempDB files were read"
            : $"{files.Count} file{(files.Count == 1 ? "" : "s")} · {files.Sum(f => f.SpaceUsedMB):N0} MB used of {files.Sum(f => f.FileSizeMB):N0} MB";

        Counters = counters.Select(Card).ToList();
        Files    = files;

        var selected = SelectedSession?.SessionId;
        Sessions.Replace(sessions.OrderByDescending(s => s.InternalObjAllocKB + s.UserObjAllocKB));
        SelectedSession = Sessions.FirstOrDefault(s => s.SessionId == selected);
    }

    private static KpiCard Card(TempDbCounter c)
    {
        double value = c.CounterValue;
        var tone = c.CounterName.Contains("Version") && value > 0 ? KpiCard.Tones.Danger
                 : c.CounterName.Contains("Free") && value < 1_000_000 ? KpiCard.Tones.Warning // < ~1 GB free (counter is in KB)
                 : KpiCard.Tones.Accent;
        var tip = c.CounterName switch
        {
            var n when n.Contains("Version Store Size")          => "Size (KB) of the version store in TempDB used by snapshot/RCSI isolation. Rapid growth indicates long-running read-committed snapshot transactions that keep row versions alive. The Performance Counters page charts how fast versions are generated and cleaned up.",
            var n when n.Contains("Free Space in tempdb")        => "Free space remaining in TempDB (KB). Low free space can cause query failures and blocking. Monitor alongside auto-grow events.",
            var n when n.Contains("Active Transactions")         => "Transactions open in TempDB right now. A long-running one keeps its row versions, and everything since, in the version store.",
            var n when n.Contains("Active Temp Tables")          => "Number of temporary tables and table variables currently open across all sessions. Very high counts may indicate connection-per-request designs or missing cleanup logic.",
            var n when n.Contains("Temp Tables For Destruction") => "Temporary tables waiting for the cleanup thread to drop them. A number that keeps climbing means cleanup can't keep up.",
            _                                                     => $"TempDB performance counter from sys.dm_os_performance_counters: {c.CounterName}."
        };
        return new KpiCard(c.CounterName, $"{value:N0}", tone, tip);
    }

    protected override void ShowLoadError(Exception exception) => ShowError("TempDB", exception);

    // The verdict's Show the session: the one holding the most, selected and in view.
    protected override void OnAction(VerdictAction action)
    {
        if (action.Key != TempDbVerdict.ShowSession || Sessions.MaxBy(s => s.NetTempDBUsageKB) is not { } top)
            return;
        SelectedSession = top;
        ShowRow?.Invoke(top);
    }
}
