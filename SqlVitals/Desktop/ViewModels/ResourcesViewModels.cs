using SqlVitals.Engine.FileIo;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels;

// The view models of the Resources pages on the template (#150): each carries its page's verdict
// and what the verdict's actions do. The pages' grids and code-behind stay as they were.

/// <summary>Memory Grants' verdict: queries waiting for memory, or how memory is used.</summary>
public sealed class MemoryViewModel(Action showWaiting, Action showClerks) : PageViewModel
{
    /// <param name="grants">The grants read with the snapshot; null for a trend reading, which reads only the snapshot.</param>
    public void Show(MemorySnapshot snapshot, IReadOnlyList<MemoryGrant>? grants) => Verdict = MemoryVerdict.From(snapshot, grants);

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case MemoryVerdict.ShowWaiting: showWaiting(); break;
            case MemoryVerdict.Clerks:      showClerks();  break;
        }
    }
}

/// <summary>TempDB's verdict: who holds TempDB, and how much room is left.</summary>
public sealed class TempDbViewModel(Action showSession) : PageViewModel
{
    public void Show(IReadOnlyList<TempDbFile> files, IReadOnlyList<TempDbSession> sessions, IReadOnlyList<TempDbCounter> counters) =>
        Verdict = TempDbVerdict.From(files, sessions, counters);

    protected override void OnAction(VerdictAction action)
    {
        if (action.Key == TempDbVerdict.ShowSession)
            showSession();
    }
}

/// <summary>File I/O's verdict: the files slower than the threshold, the worst first.</summary>
public sealed class FileIoViewModel(Action showSlow) : PageViewModel
{
    public void Show(FileLatencyList? list) => Verdict = FileIoVerdict.From(list);

    protected override void OnAction(VerdictAction action)
    {
        if (action.Key == FileIoVerdict.ShowSlow)
            showSlow();
    }
}

/// <summary>DB Storage's verdict: files near their maximum, auto-shrink, or the storage at a glance.</summary>
public sealed class DbStorageViewModel(Action showFile) : PageViewModel
{
    public void Show(IReadOnlyList<DatabaseFile> files, IReadOnlyList<TempDbFileSummary> tempDb) => Verdict = DbStorageVerdict.From(files, tempDb);

    protected override void OnAction(VerdictAction action)
    {
        if (action.Key == DbStorageVerdict.ShowFile)
            showFile();
    }
}

/// <summary>Perfmon's verdict: counters past a known limit, and the ones rising.</summary>
public sealed class PerfmonViewModel(Action<string> openPage) : PageViewModel
{
    private string? _causePage;

    public void Show(IReadOnlyList<PerfmonSeries> series, WaitTrendState state, string? noData)
    {
        Verdict    = PerfmonVerdict.From(series, state, noData);
        _causePage = PerfmonVerdict.Breached(series)?.Page;
    }

    protected override void OnAction(VerdictAction action)
    {
        if (action.Key == PerfmonVerdict.Cause && _causePage is { } page)
            openPage(page);
    }
}
