using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Backups;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

/// <summary>
/// Backups (#40, #184): each database's last full, differential and log backup from msdb, with a
/// warning when there's none or one is older than the RPO typed on the page, and the backups msdb
/// keeps for the database selected. The verdict names the databases past the RPO (#146). Not
/// reachable on Azure SQL Database, which takes its own backups: the sidebar disables it, and the
/// repository says so too.
/// </summary>
public sealed partial class BackupsViewModel : PageViewModel
{
    /// <summary>The filter the verdict's "Show only these" picks.</summary>
    internal const int NeedsAttentionFilter = 1;

    private static readonly (string Label, Func<DatabaseBackup, bool> Keep, string Empty)[] Filters =
    [
        ("All databases",                        _ => true,
            "There are no databases to show."),
        ("Needs attention: missing or overdue",  d => d.NeedsAttention,
            "Every online database has a full backup, and log backups where it needs them, within the ages set above."),
        ("FULL or BULK_LOGGED recovery",         d => d.UsesLogBackups,
            "No database uses the FULL or BULK_LOGGED recovery model."),
        ("SIMPLE recovery",                      d => !d.UsesLogBackups,
            "No database uses the SIMPLE recovery model."),
        ("Not online (not checked)",             d => !d.IsOnline,
            "Every database is online."),
    ];

    /// <summary>How a part of the summary line is shown: what a DBA has to act on stands out.</summary>
    public enum SummaryTone { Plain, Bold, Danger, Warning, Muted }

    /// <summary>"2 full overdue", in amber when it isn't 0.</summary>
    public sealed record SummaryPart(string Text, SummaryTone Tone);

    private readonly IBackupRepository _repo;
    private readonly ConnectionSettingsService _settings;
    private readonly TimeProvider _time;

    // The RPO checked against: the one saved, or the one typed last.
    private BackupRpo _rpo;

    // What was read last; changing the filter only filters it again.
    private BackupList? _backups;

    // While the rows are replaced: the grid clears its selection before the database is selected again.
    private bool _showing;

    // Cancels the history read for the database selected: a new selection, or leaving the page.
    private Action? _cancelHistory;

    // The database whose backups History holds; a refresh of the same one keeps them while it reads.
    private string? _historyOf;

    public BackupsViewModel(IBackupRepository repo, ConnectionSettingsService settings, TimeProvider? time = null)
    {
        _repo     = repo;
        _settings = settings;
        _time     = time ?? TimeProvider.System;
        _rpo      = settings.Load().BackupRpo;
        FullAgeText = BackupRpo.Format(_rpo.FullMaxAge);
        LogAgeText  = BackupRpo.Format(_rpo.LogMaxAge);
    }

    // ── The list ──────────────────────────────────────────────────────────

    /// <summary>Which databases to list: the combo's choices.</summary>
    public IReadOnlyList<string> FilterNames { get; } = Filters.Select(f => f.Label).ToList();

    [ObservableProperty]
    private int _filterIndex;

    partial void OnFilterIndexChanged(int value) => Show();

    /// <summary>The databases the filter keeps, the ones to act on first.</summary>
    public GridRows<DatabaseBackup> Databases { get; } = [];

    /// <summary>"31 databases", "3 of 31 databases".</summary>
    [ObservableProperty]
    private string _countText = "Databases";

    /// <summary>Why no database is listed: none matches the filter, or msdb couldn't be read.</summary>
    [ObservableProperty]
    private string? _noDataText;

    [ObservableProperty]
    private bool _showsNoData;

    /// <summary>"31 databases · 1 without a full backup · 2 full overdue · …"; empty while nothing could be read.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsSummary))]
    private IReadOnlyList<SummaryPart> _summary = [];

    public bool ShowsSummary => Summary.Count > 0;

    /// <summary>Folded, the RPO and counts say it in one line (#146).</summary>
    [ObservableProperty]
    private string? _foldedSummary;

    /// <summary>Something that kept some backups from being checked, though the list was read.</summary>
    [ObservableProperty]
    private string? _problem;

    /// <summary>When it read, what it checked against and how to read the times; "Reading…" the first time.</summary>
    [ObservableProperty]
    private string _readNote = string.Empty;

    protected override async Task LoadAsync(CancellationToken cancel)
    {
        if (!ReadRpo())
            return;
        if (_backups is null)
            ReadNote = "Reading the backup history from msdb…";
        var backups = await _repo.GetBackupStatusAsync(_rpo, cancel);
        _backups = backups;
        Verdict  = BackupsVerdict.From(backups);
        Show();
    }

    protected override void ShowLoadError(Exception exception) => ShowError("the backup history", exception);

    private void Show()
    {
        if (_backups is not { } list)
            return;

        var filter = Filters[Math.Clamp(FilterIndex, 0, Filters.Length - 1)];
        var shown  = list.Databases.Where(filter.Keep).ToList();

        // Keep the same database across a refresh; otherwise the first, the most urgent.
        var selected = SelectedDatabase?.Name;
        _showing = true;
        try
        {
            Databases.Replace(shown);
        }
        finally
        {
            _showing = false;
        }
        Select(shown.FirstOrDefault(d => d.Name == selected) ?? shown.FirstOrDefault());

        CountText = !list.Available ? "Databases"
            : shown.Count == list.Databases.Count ? $"{list.Databases.Count:N0} database{(list.Databases.Count == 1 ? "" : "s")}"
            : $"{shown.Count:N0} of {list.Databases.Count:N0} databases";

        Summary       = SummaryOf(list);
        FoldedSummary = list.Available ? string.Concat(Summary.Select(p => p.Text)) : list.Problem;
        Problem       = list.Available ? list.Problem : null;
        ReadNote      = ReadNoteOf(list);
        NoDataText    = !list.Available ? list.Problem : filter.Empty;
        ShowsNoData   = shown.Count == 0;
    }

    // "31 databases · 1 without a full backup · 2 full overdue · 3 log backups missing or overdue · 1 not online".
    private static IReadOnlyList<SummaryPart> SummaryOf(BackupList list)
    {
        if (!list.Available)
            return [];

        var parts = new List<SummaryPart>();
        void Add(string text, SummaryTone tone = SummaryTone.Plain) => parts.Add(new SummaryPart(text, tone));
        void Count(int count, string text, SummaryTone tone) => Add(text, count > 0 ? tone : SummaryTone.Plain);
        const string separator = "  ·  ";

        Add($"{list.Databases.Count:N0} database{(list.Databases.Count == 1 ? "" : "s")}", SummaryTone.Bold);
        Add(separator, SummaryTone.Muted);
        Count(list.NoFullCount, $"{list.NoFullCount:N0} without a full backup", SummaryTone.Danger);
        Add(separator, SummaryTone.Muted);
        Count(list.FullOverdueCount, $"{list.FullOverdueCount:N0} full overdue", SummaryTone.Warning);
        Add(separator, SummaryTone.Muted);
        Count(list.LogProblemCount, $"{list.LogProblemCount:N0} log backup{(list.LogProblemCount == 1 ? "" : "s")} missing or overdue", SummaryTone.Warning);
        if (list.NotCheckedCount > 0)
        {
            Add(separator, SummaryTone.Muted);
            Add($"{list.NotCheckedCount:N0} not online", SummaryTone.Muted);
        }
        return parts;
    }

    private string ReadNoteOf(BackupList list) =>
        !list.Available ? string.Empty
        : $"Read at {_time.GetLocalNow():HH:mm:ss}. Checked for {list.Rpo.Describe()}. Times are the server's local time, as msdb " +
          "keeps them" + (list.ServerNow is { } now ? $" (it was {now:yyyy-MM-dd HH:mm} there)" : "") +
          ". Only backups msdb recorded on this server count, and only those since each database was created or restored.";

    // The verdict's "Show only these": the databases that need attention.
    protected override void OnAction(VerdictAction action)
    {
        if (action.Key == BackupsVerdict.ShowThem)
            FilterIndex = NeedsAttentionFilter;
    }

    // ── RPO ───────────────────────────────────────────────────────────────

    /// <summary>The full backup age warned about: "7", "26h", "1d 12h".</summary>
    [ObservableProperty]
    private string _fullAgeText = string.Empty;

    /// <summary>The log backup age warned about: "60", "2h", "90 min".</summary>
    [ObservableProperty]
    private string _logAgeText = string.Empty;

    /// <summary>Why the ages typed can't be used; null when they can.</summary>
    [ObservableProperty]
    private string? _rpoError;

    /// <summary>
    /// Another RPO: reads again, after a read in progress, which judged the old one (#169). Never
    /// disabled: Apply or Enter pressed while it reads, the ages typed by then are read next.
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ApplyAsync() => RereadAsync();

    // Reads the two boxes, saving them when they changed. False, with the reason shown, when
    // they can't be used.
    private bool ReadRpo()
    {
        if (!BackupRpo.TryParse(FullAgeText, LogAgeText, out var rpo, out var error))
        {
            RpoError = error;
            return false;
        }

        RpoError    = null;
        FullAgeText = BackupRpo.Format(rpo!.FullMaxAge);
        LogAgeText  = BackupRpo.Format(rpo.LogMaxAge);
        if (rpo != _rpo)
        {
            _rpo = rpo;
            try
            {
                _settings.SetBackupRpo(rpo);
            }
            catch (Exception ex)
            {
                // The check still uses what was typed; it just won't be remembered.
                AppLog.Error("Backups: saving the RPO failed", ex);
            }
        }
        return true;
    }

    // ── The database selected ─────────────────────────────────────────────

    /// <summary>The database whose backups are shown; kept across a refresh by its name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private DatabaseBackup? _selectedDatabase;

    public bool HasSelection => SelectedDatabase is not null;

    // Replacing the rows clears the grid's selection; the database is selected again after.
    partial void OnSelectedDatabaseChanged(DatabaseBackup? value)
    {
        if (!_showing)
            ShowDatabase(value);
    }

    private void Select(DatabaseBackup? db)
    {
        var changed = !ReferenceEquals(SelectedDatabase, db);
        SelectedDatabase = db;
        if (!changed)
            ShowDatabase(db);
    }

    [ObservableProperty]
    private string _detailHeader = NoneSelected;

    [ObservableProperty]
    private string _detailText = string.Empty;

    [ObservableProperty]
    private string _historyLabel = "Backups msdb keeps, newest first";

    /// <summary>The backups msdb keeps for the database selected, newest first.</summary>
    public GridRows<BackupHistoryEntry> History { get; } = [];

    /// <summary>The history read for the database selected last; done when there's none.</summary>
    internal Task HistoryRead { get; private set; } = Task.CompletedTask;

    private const string NoneSelected = "Select a database above to see its backups.";

    private const string ReadingHistory = "Reading the backups msdb keeps…";

    private void ShowDatabase(DatabaseBackup? db)
    {
        CancelHistory();

        if (db is null)
        {
            DetailHeader = NoneSelected;
            DetailText   = string.Empty;
            History.Replace([]);
            _historyOf   = null;
            HistoryRead  = Task.CompletedTask;
            return;
        }

        DetailHeader = $"{db.Name} · {db.StatusText} · {db.RecoveryModel} recovery";
        DetailText   = DetailOf(db);
        if (db.Name != _historyOf)
        {
            History.Replace([]);
            _historyOf   = db.Name;
            HistoryLabel = ReadingHistory;
        }
        HistoryRead  = ReadHistoryAsync(db);
    }

    private void CancelHistory()
    {
        try
        {
            _cancelHistory?.Invoke();
        }
        catch (ObjectDisposedException)
        {
            // It had just finished.
        }
        _cancelHistory = null;
    }

    // Never throws: a failure is said above the history, and logged.
    private async Task ReadHistoryAsync(DatabaseBackup db)
    {
        using var source = new CancellationTokenSource();
        var cancel = source.Token;
        Action cancelThis = source.Cancel;
        _cancelHistory = cancelThis;
        try
        {
            await ShowHistoryAsync(db, cancel);
        }
        finally
        {
            if (ReferenceEquals(_cancelHistory, cancelThis))
                _cancelHistory = null;
        }
    }

    private async Task ShowHistoryAsync(DatabaseBackup db, CancellationToken cancel)
    {
        IReadOnlyList<BackupHistoryEntry> history;
        try
        {
            history = await _repo.GetBackupHistoryAsync(db.Name, db.Row.CreateDate, cancel);
        }
        catch (Exception) when (cancel.IsCancellationRequested)
        {
            // Another database was selected, or the page left: nothing to say.
            return;
        }
        catch (Exception ex)
        {
            AppLog.Error("Backups: reading a database's backup history failed", ex);
            History.Replace([]);
            HistoryLabel = "Its backups could not be read: " + ErrorStatus.Format(ex);
            return;
        }
        if (cancel.IsCancellationRequested)
            return;

        History.Replace(history);
        var older = history.Count(h => h.BeforeCreate);
        HistoryLabel = history.Count switch
        {
            0 => "msdb keeps no backups of this database",
            var n when n >= BackupHistoryEntry.MaxRows => $"The newest {n:N0} backups msdb keeps, newest first",
            var n => $"{n:N0} backup{(n == 1 ? "" : "s")} msdb keeps, newest first",
        } + (older > 0
            ? $" · {older:N0} in grey finished before it was created or restored on {db.Row.CreateDate:yyyy-MM-dd HH:mm}, so don't count"
            : "");
    }

    protected override void OnLeaving() => CancelHistory();

    private static string DetailOf(DatabaseBackup db)
    {
        var parts = new List<string>();
        if (db.WarningsText.Length > 0)
            parts.Add(db.WarningsText);
        else
            parts.Add("Its backups are within the ages set above.");

        if (db.RecoveryPoint is { } point)
            parts.Add($"Newest point it can be restored to: {point:yyyy-MM-dd HH:mm:ss} ({DatabaseBackup.FormatAge(db.DataAtRisk)} ago).");

        if (db.IsOnline && !db.UsesLogBackups)
            parts.Add("SIMPLE recovery: it can only be restored to its last full or differential backup.");
        else if (db.UsesLogBackups && !db.NeedsLogBackups)
            parts.Add(db.Row.IsReadOnly
                ? "Read-only, so its log doesn't change and log backups aren't checked."
                : "model's recovery model is only what new databases start with, so its log backups aren't checked.");

        if (db.Full is { } full)
            parts.Add((full.CopyOnly
                          ? "The last full backup was copy-only: it can be restored, but differentials are based on the full backup before it."
                          : $"The last full backup took {DatabaseBackup.FormatAge(full.Duration)}.") +
                      (string.IsNullOrEmpty(full.Device) ? "" : $" Written to {full.Device}."));

        if (db.Row.InAvailabilityGroup)
            parts.Add("It's in an availability group: backups taken on another replica are only in that replica's msdb.");

        return string.Join(" ", parts);
    }
}
