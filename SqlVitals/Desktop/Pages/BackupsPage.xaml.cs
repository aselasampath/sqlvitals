using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Backups;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Each database's last full, differential and log backup from msdb (#40), with a warning when
/// there's none, or one is older than the RPO set on the page. Selecting a database shows the
/// backups msdb keeps for it. Not reachable on Azure SQL Database, which takes its own backups:
/// MainWindow disables the button, and the repository says so too.
/// </summary>
public partial class BackupsPage : Page, IRefreshable
{
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

    private readonly IWaitStatsRepository      _repo;
    private readonly ConnectionSettingsService _settings;

    private BackupRpo _rpo;

    // What was read last; changing the filter only filters it again.
    private BackupList? _backups;

    // Set once the filter choices are filled in, so filling them doesn't filter.
    private readonly bool _ready;

    private bool _refreshing;

    // Bumped per database selection, so the history of one selected earlier doesn't land on a later one.
    private int _historyVersion;

    // While Show() replaces the rows, which clears the selection before it selects the database again.
    private bool _showing;

    public BackupsPage(IWaitStatsRepository repo, ConnectionSettingsService settings)
    {
        _repo     = repo;
        _settings = settings;
        InitializeComponent();

        foreach (var (label, _, _) in Filters)
            CmbFilter.Items.Add(label);
        CmbFilter.SelectedIndex = 0;

        _rpo = settings.Load().BackupRpo;
        ShowRpo(_rpo);

        _ready = true;
    }

    /// <summary>
    /// Reads the backups from msdb, checked against the ages in the boxes. Called on navigate, by
    /// the sidebar Refresh, Read again and Apply.
    /// </summary>
    public async Task RefreshAsync()
    {
        if (_refreshing || !ReadRpo()) return;
        _refreshing = true;
        BtnReload.IsEnabled = false;
        BtnApply.IsEnabled  = false;
        if (_backups is null)
            TxtStatus.Text = "Reading the backup history from msdb…";
        try
        {
            _backups = await _repo.GetBackupStatusAsync(_rpo);
            Show();
        }
        finally
        {
            _refreshing = false;
            BtnReload.IsEnabled = true;
            BtnApply.IsEnabled  = true;
        }
    }

    private void Show()
    {
        if (_backups is not { } list) return;

        var filter = Filters[Math.Max(0, CmbFilter.SelectedIndex)];
        var shown  = list.Databases.Where(filter.Keep).ToList();

        // Keep the same database across a refresh; otherwise the first, the most urgent.
        var selected = (DatabasesGrid.SelectedItem as DatabaseBackup)?.Name;
        _showing = true;
        try
        {
            DataGridRefresh.SetItemsSource(DatabasesGrid, shown);
            DatabasesGrid.SelectedItem = shown.FirstOrDefault(d => d.Name == selected) ?? shown.FirstOrDefault();
        }
        finally
        {
            _showing = false;
        }

        TxtCount.Text = !list.Available ? "Databases"
            : shown.Count == list.Databases.Count ? $"{list.Databases.Count:N0} database{(list.Databases.Count == 1 ? "" : "s")}"
            : $"{shown.Count:N0} of {list.Databases.Count:N0} databases";

        ShowSummary(list);

        var problem = list.Available ? list.Problem : null;
        TxtProblem.Text       = problem ?? string.Empty;
        TxtProblem.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
        TxtStatus.Text        = StatusText(list);

        TxtNoData.Text       = !list.Available ? list.Problem : filter.Empty;
        TxtNoData.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // "31 databases · 1 without a full backup · 2 full overdue · 3 log backups missing or overdue · 1 not online".
    private void ShowSummary(BackupList list)
    {
        TxtSummary.Inlines.Clear();
        if (!list.Available)
        {
            TxtSummary.Visibility = Visibility.Collapsed;
            return;
        }
        TxtSummary.Visibility = Visibility.Visible;

        void Add(string text, Brush? brush = null, bool bold = false)
        {
            var run = new Run(text) { FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
            if (brush is not null) run.Foreground = brush;
            TxtSummary.Inlines.Add(run);
        }
        var danger  = (Brush)FindResource("Danger");
        var warning = (Brush)FindResource("Warning");
        var muted   = (Brush)FindResource("TextMuted");

        Add($"{list.Databases.Count:N0} database{(list.Databases.Count == 1 ? "" : "s")}", bold: true);
        Add("  ·  ", muted);
        Add($"{list.NoFullCount:N0} without a full backup", list.NoFullCount > 0 ? danger : null, list.NoFullCount > 0);
        Add("  ·  ", muted);
        Add($"{list.FullOverdueCount:N0} full overdue", list.FullOverdueCount > 0 ? warning : null, list.FullOverdueCount > 0);
        Add("  ·  ", muted);
        Add($"{list.LogProblemCount:N0} log backup{(list.LogProblemCount == 1 ? "" : "s")} missing or overdue",
            list.LogProblemCount > 0 ? warning : null, list.LogProblemCount > 0);
        if (list.NotCheckedCount > 0)
        {
            Add("  ·  ", muted);
            Add($"{list.NotCheckedCount:N0} not online", muted);
        }
    }

    private static string StatusText(BackupList list) =>
        !list.Available ? string.Empty
        : $"Read at {DateTime.Now:HH:mm:ss}. Checked for {list.Rpo.Describe()}. Times are the server's local time, as msdb " +
          "keeps them" + (list.ServerNow is { } now ? $" (it was {now:yyyy-MM-dd HH:mm} there)" : "") +
          ". Only backups msdb recorded on this server count, and only those since each database was created or restored.";

    // ── RPO ───────────────────────────────────────────────────────────────────

    private void ShowRpo(BackupRpo rpo)
    {
        TxtFullAge.Text = BackupRpo.Format(rpo.FullMaxAge);
        TxtLogAge.Text  = BackupRpo.Format(rpo.LogMaxAge);
    }

    // Reads the two boxes, saving them when they changed. False, with the reason shown, when
    // they can't be used.
    private bool ReadRpo()
    {
        if (!BackupRpo.TryParse(TxtFullAge.Text, TxtLogAge.Text, out var rpo, out var error))
        {
            TxtError.Text       = error;
            TxtError.Visibility = Visibility.Visible;
            return false;
        }

        TxtError.Visibility = Visibility.Collapsed;
        ShowRpo(rpo!);
        if (rpo! != _rpo)
        {
            _rpo = rpo!;
            try
            {
                _settings.SetBackupRpo(rpo!);
            }
            catch (Exception ex)
            {
                // The check still uses what was typed; it just won't be remembered.
                AppLog.Error("Backups: saving the RPO failed", ex);
            }
        }
        return true;
    }

    private async void BtnApply_Click(object sender, RoutedEventArgs e) => await ReadAgainAsync();

    private async void Rpo_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        await ReadAgainAsync();
    }

    // ── Toolbar ───────────────────────────────────────────────────────────────

    private void CmbFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) Show();
    }

    // The sidebar Refresh reports errors through MainWindow; a read started here reports its own.
    private async void BtnReload_Click(object sender, RoutedEventArgs e) => await ReadAgainAsync();

    private async Task ReadAgainAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Backups: read failed", ex);
            TxtStatus.Text = MainWindow.FormatErrorStatus(ex);
        }
    }

    // ── Selected database ─────────────────────────────────────────────────────

    private async void DatabasesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_showing && DatabasesGrid.SelectedItem is null)
            return;

        var version = ++_historyVersion;

        if (DatabasesGrid.SelectedItem is not DatabaseBackup db)
        {
            DetailPanel.IsEnabled   = false;
            TxtDetailHeader.Text    = "Select a database above to see its backups.";
            TxtDetailSub.Text       = string.Empty;
            HistoryGrid.ItemsSource = null;
            return;
        }

        DetailPanel.IsEnabled = true;
        TxtDetailHeader.Text  = $"{db.Name} · {db.StatusText} · {db.RecoveryModel} recovery";
        TxtDetailSub.Text     = DetailText(db);

        IReadOnlyList<BackupHistoryEntry> history;
        try
        {
            history = await _repo.GetBackupHistoryAsync(db.Name, db.Row.CreateDate);
        }
        catch (Exception ex)
        {
            AppLog.Error("Backups: reading a database's backup history failed", ex);
            if (version != _historyVersion) return;
            HistoryGrid.ItemsSource = null;
            TxtHistoryLabel.Text    = "Its backups could not be read: " + MainWindow.FormatErrorStatus(ex);
            return;
        }
        if (version != _historyVersion) return;

        HistoryGrid.ItemsSource = history;
        var older = history.Count(h => h.BeforeCreate);
        TxtHistoryLabel.Text = history.Count switch
        {
            0 => "msdb keeps no backups of this database",
            var n when n >= BackupHistoryEntry.MaxRows => $"The newest {n:N0} backups msdb keeps, newest first",
            var n => $"{n:N0} backup{(n == 1 ? "" : "s")} msdb keeps, newest first",
        } + (older > 0
            ? $" · {older:N0} in grey finished before it was created or restored on {db.Row.CreateDate:yyyy-MM-dd HH:mm}, so don't count"
            : "");
    }

    private static string DetailText(DatabaseBackup db)
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
