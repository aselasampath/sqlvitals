using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Deadlocks;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Recent deadlocks from the system_health Extended Events session (#38, #187): the victim, the
/// other participants, the objects and statements involved, and which deadlocks keep coming back.
/// Each one can be copied as XML or saved as an .xdl file to open as a graph in SSMS. Read once and
/// on Refresh; changing the range only filters what was read. The verdict (#149) counts the
/// deadlocks in the range and names the object fought over most.
/// </summary>
public sealed partial class DeadlocksViewModel(IDeadlockRepository repo, IDialogService dialogs, IFileDialogs files,
                                               IClipboard clipboard, TimeProvider? time = null) : PageViewModel
{
    private static readonly (string Label, TimeSpan? Span)[] Ranges =
    [
        ("Last hour",                   TimeSpan.FromHours(1)),
        ("Last 24 hours",               TimeSpan.FromDays(1)),
        ("Last 7 days",                 TimeSpan.FromDays(7)),
        ("Last 30 days",                TimeSpan.FromDays(30)),
        ("Everything system_health has", null),
    ];

    /// <summary>The range that shows every deadlock read: the verdict's Show all picks it.</summary>
    internal static readonly int EverythingRange = Ranges.Length - 1;

    /// <summary>The tabs: every deadlock, and the recurring patterns.</summary>
    internal const int DeadlocksTab = 0, RecurringTab = 1;

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    // What was read last; changing the range only filters it again.
    private DeadlockHistory? _history;

    // The most frequent pattern in the range, for the verdict's Open the newest.
    private DeadlockPattern? _topPattern;

    // ── The range, the tabs and the source ────────────────────────────────

    /// <summary>The periods the combo offers, ending now.</summary>
    public IReadOnlyList<string> RangeNames { get; } = Ranges.Select(r => r.Label).ToList();

    [ObservableProperty]
    private int _rangeIndex = EverythingRange;

    partial void OnRangeIndexChanged(int value) => Show();

    /// <summary>The tab shown: <see cref="DeadlocksTab"/> or <see cref="RecurringTab"/>.</summary>
    [ObservableProperty]
    private int _tabIndex = DeadlocksTab;

    /// <summary>Why nothing, or less than the event files, could be read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsProblem))]
    private string? _problem;

    public bool ShowsProblem => Problem is not null;

    /// <summary>Where the history was read from, how far back it reaches, and what was left out.</summary>
    [ObservableProperty]
    private string _readNote = string.Empty;

    /// <summary>Folded, the source says it in one line (#149): "system_health's event files · oldest held 2026-09-12 08:14".</summary>
    [ObservableProperty]
    private string? _sourceSummary;

    // ── Every deadlock, and the recurring patterns ────────────────────────

    /// <summary>The deadlocks in the range, newest first.</summary>
    public GridRows<DeadlockReport> Deadlocks { get; } = [];

    /// <summary>The same deadlock again and again: the most frequent pattern first.</summary>
    public GridRows<DeadlockPattern> Patterns { get; } = [];

    /// <summary>"Deadlocks", "1 deadlock", "12 deadlocks".</summary>
    [ObservableProperty]
    private string _countText = "Deadlocks";

    /// <summary>"4 patterns, 2 seen more than once".</summary>
    [ObservableProperty]
    private string _patternCountText = "Deadlock patterns";

    /// <summary>Why no deadlock is listed: none in the range, none held, or nothing could be read.</summary>
    [ObservableProperty]
    private string? _noDataText;

    [ObservableProperty]
    private bool _showsNoData;

    /// <summary>A deadlock to show: a pattern's Open newest selected it. The view scrolls to it.</summary>
    public event Action<DeadlockReport>? ShowRow;

    protected override async Task LoadAsync(CancellationToken cancel)
    {
        if (_history is null)
            ReadNote = "Reading deadlock reports from system_health…";
        _history = await repo.GetDeadlockHistoryAsync(cancel);
        Show();
    }

    // Nothing read yet: the verdict says why. Later, the deadlocks read last stay shown, and the top bar says it failed.
    protected override void ShowLoadError(Exception exception)
    {
        if (_history is not null)
            return;
        ReadNote = string.Empty;
        ShowError("the deadlock history", exception);
    }

    private (string Label, TimeSpan? Span) Range => Ranges[Math.Clamp(RangeIndex, 0, Ranges.Length - 1)];

    // "in the last 24 hours", "in what system_health holds".
    private string During => Range.Span is null ? "in what system_health holds" : $"in the {Range.Label.ToLowerInvariant()}";

    private void Show()
    {
        if (_history is not { } history)
            return;

        var from    = Range.Span is { } span ? _time.GetUtcNow().UtcDateTime - span : DateTime.MinValue;
        var inRange = history.Deadlocks.Where(d => d.TimestampUtc >= from).ToList();
        var (patterns, reports) = DeadlockPatterns.Group(inRange);

        // Keep the same deadlock selected when only the range changed, or across a refresh; otherwise the newest.
        var selected = SelectedDeadlock;
        Deadlocks.Replace(reports);
        Patterns.Replace(patterns);
        SelectedDeadlock = reports.FirstOrDefault(r => selected is not null && SameDeadlock(r, selected))
                           ?? (reports.Count > 0 ? reports[0] : null);

        CountText = reports.Count switch
        {
            0     => "Deadlocks",
            1     => "1 deadlock",
            var n => $"{n:N0} deadlocks",
        };
        var recurring = patterns.Count(p => p.Count > 1);
        PatternCountText = patterns.Count == 0 ? "Deadlock patterns"
            : $"{patterns.Count:N0} pattern{(patterns.Count == 1 ? "" : "s")}, {recurring:N0} seen more than once";

        Problem     = history.Problem;
        ReadNote    = StatusText(history);
        NoDataText  = EmptyText(history, reports.Count);
        ShowsNoData = reports.Count == 0;

        _topPattern = patterns.Count > 0 ? patterns[0] : null;
        Verdict     = DeadlocksVerdict.From(history, reports, patterns, During);

        SourceSummary = history.Source switch
        {
            DeadlockSource.None      => history.Problem ?? "Nothing could be read",
            DeadlockSource.EventFile => "system_health's event files",
            _                        => "system_health's ring buffer",
        } + (history.Deadlocks.Count > 0 && history.Source != DeadlockSource.None ? $" · oldest held {history.Deadlocks[^1].Time:yyyy-MM-dd HH:mm}" : "")
          + (history.Problem is not null && history.Source != DeadlockSource.None ? " · read in part" : "");
    }

    private static bool SameDeadlock(DeadlockReport a, DeadlockReport b) =>
        a.TimestampUtc == b.TimestampUtc && a.Xml == b.Xml;

    private static string StatusText(DeadlockHistory history)
    {
        if (history.Source == DeadlockSource.None)
            return string.Empty;

        var source = history.Source == DeadlockSource.EventFile
            ? $"Read from system_health's event files ({history.Location}), which roll over: how far back they reach depends on how busy the server is."
            : "Read from system_health's ring buffer, which keeps only the most recent events and is emptied when SQL Server restarts.";

        var text = new StringBuilder(source);
        if (history.Deadlocks.Count > 0)
            text.Append(CultureInfo.CurrentCulture, $" Oldest deadlock held: {history.Deadlocks[^1].Time:yyyy-MM-dd HH:mm}.");
        if (history.TotalFound > history.Deadlocks.Count)
            text.Append(CultureInfo.CurrentCulture, $" Showing the newest {history.Deadlocks.Count:N0} of {history.TotalFound:N0}.");
        if (history.Unreadable > 0)
            text.Append(CultureInfo.CurrentCulture, $" {history.Unreadable:N0} report{(history.Unreadable == 1 ? "" : "s")} could not be read.");
        return text.ToString();
    }

    private string EmptyText(DeadlockHistory history, int shown)
    {
        if (shown > 0) return string.Empty;
        if (history.Source == DeadlockSource.None) return history.Problem ?? "No deadlock history could be read.";
        if (history.Deadlocks.Count == 0) return "system_health holds no deadlock reports: no deadlock has happened in the time it covers.";
        return $"No deadlocks in the {Range.Label.ToLowerInvariant()}. " +
               $"system_health holds {history.Deadlocks.Count:N0} older: choose a longer range to see them.";
    }

    protected override void OnAction(VerdictAction action)
    {
        switch (action.Key)
        {
            case DeadlocksVerdict.OpenNewest when _topPattern is { } top: OpenNewest(top);              break;
            case DeadlocksVerdict.Recurring:                              TabIndex   = RecurringTab;    break;
            case DeadlocksVerdict.ShowAll:                                RangeIndex = EverythingRange; break;
        }
    }

    /// <summary>A pattern's newest deadlock: on the Deadlocks tab, selected and in view.</summary>
    [RelayCommand]
    private void OpenNewest(DeadlockPattern? pattern)
    {
        if (pattern is null || Deadlocks.FirstOrDefault(r => SameDeadlock(r, pattern.Newest)) is not { } row)
            return;
        TabIndex         = DeadlocksTab;
        SelectedDeadlock = row;
        ShowRow?.Invoke(row);
    }

    // ── The deadlock selected: who took part, over what, and their statements ──

    private const string NoneSelected = "Select a deadlock above to see who took part.";

    /// <summary>The deadlock whose participants are below; kept across a refresh by its time and report.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDeadlock))]
    [NotifyCanExecuteChangedFor(nameof(CopyXmlCommand), nameof(SaveXdlCommand))]
    private DeadlockReport? _selectedDeadlock;

    public bool HasDeadlock => SelectedDeadlock is not null;

    partial void OnSelectedDeadlockChanged(DeadlockReport? value)
    {
        if (value is null)
        {
            DetailHeader = NoneSelected;
            Processes.Replace([]);
            Resources.Replace([]);
            SelectedProcess = null;
            return;
        }

        DetailHeader = $"Deadlock at {value.Time:yyyy-MM-dd HH:mm:ss.fff} · {value.SessionCount} session" +
                       $"{(value.SessionCount == 1 ? " (parallel query)" : "s")} · {value.Resources.Count} resource" +
                       $"{(value.Resources.Count == 1 ? "" : "s")}";

        // Victims first: the session the application saw fail.
        var processes = value.Processes.OrderByDescending(p => p.IsVictim).ToList();
        Processes.Replace(processes);
        Resources.Replace(value.Resources);
        SelectedProcess = processes.FirstOrDefault();
    }

    /// <summary>"Deadlock at 2026-10-13 09:41:12.345 · 2 sessions · 2 resources".</summary>
    [ObservableProperty]
    private string _detailHeader = NoneSelected;

    /// <summary>The processes of the deadlock selected, victims first.</summary>
    public GridRows<DeadlockProcess> Processes { get; } = [];

    /// <summary>The locks they fought over.</summary>
    public GridRows<DeadlockResource> Resources { get; } = [];

    /// <summary>The participant whose statement is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatementText))]
    private DeadlockProcess? _selectedProcess;

    /// <summary>The participant's statement and input buffer, as SQL comments and text.</summary>
    public string StatementText => SelectedProcess is { } p ? Statement(p) : string.Empty;

    private static string Statement(DeadlockProcess p)
    {
        var text = new StringBuilder();
        text.AppendLine($"-- {p.SessionText}{(p.IsVictim ? " (victim)" : "")}" +
                        (p.LoginName is null ? "" : $" · {p.LoginName}") +
                        (p.LastBatchStarted is { } started ? $" · batch started {started:yyyy-MM-dd HH:mm:ss} (server time)" : ""));

        if (p.Statement is not null)
        {
            text.AppendLine(p.ProcedureName is null ? "-- Statement" : $"-- Statement in {p.ProcedureName}");
            text.AppendLine(p.Statement);
            text.AppendLine();
        }

        if (p.InputBuffer is not null)
        {
            text.AppendLine("-- Input buffer (the batch the client sent)");
            text.AppendLine(p.InputBuffer);
        }

        if (p.Statement is null && p.InputBuffer is null)
            text.AppendLine("-- The report has no statement text for this process.");

        return text.ToString();
    }

    /// <summary>Copies the deadlock report's XML.</summary>
    [RelayCommand(CanExecute = nameof(HasDeadlock))]
    private void CopyXml()
    {
        if (SelectedDeadlock is { } report)
            clipboard.SetText(report.Xml);
    }

    /// <summary>Saves the deadlock graph as an .xdl file, which SSMS opens as a graph.</summary>
    [RelayCommand(CanExecute = nameof(HasDeadlock))]
    private void SaveXdl()
    {
        if (SelectedDeadlock is not { } report)
            return;

        var path = files.Save(new FileDialogOptions(
            "Save Deadlock Graph", "Deadlock graph (*.xdl)|*.xdl|XML file (*.xml)|*.xml")
        {
            FileName = $"Deadlock_{report.Time:yyyyMMdd_HHmmss}",
        });
        if (path is null)
            return;

        try
        {
            File.WriteAllText(path, report.Xml, Encoding.UTF8);
            dialogs.Inform(
                "Deadlock graph saved.\nOpen it in SSMS (File → Open → File…) to see the processes and resources drawn as a graph.",
                "Saved");
        }
        catch (Exception ex)
        {
            dialogs.Error($"Failed to save the file:\n{ex.Message}", "Save Failed");
        }
    }
}
