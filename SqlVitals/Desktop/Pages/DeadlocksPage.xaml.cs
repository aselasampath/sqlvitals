using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Deadlocks;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Recent deadlocks from the system_health Extended Events session (#38): the victim, the other
/// participants, the objects and statements involved, and which deadlocks keep coming back.
/// Each one can be saved as an .xdl file to open as a graph in SSMS.
/// </summary>
public partial class DeadlocksPage : Page, IRefreshable
{
    private static readonly (string Label, TimeSpan? Span)[] Ranges =
    [
        ("Last hour",                   TimeSpan.FromHours(1)),
        ("Last 24 hours",               TimeSpan.FromDays(1)),
        ("Last 7 days",                 TimeSpan.FromDays(7)),
        ("Last 30 days",                TimeSpan.FromDays(30)),
        ("Everything system_health has", null),
    ];

    private readonly IWaitStatsRepository _repo;

    // What was read last; changing the range only filters it again.
    private DeadlockHistory? _history;

    // Set once the range choices are filled in, so filling them doesn't filter.
    private readonly bool _ready;

    public DeadlocksPage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();

        foreach (var (label, _) in Ranges)
            CmbRange.Items.Add(label);
        CmbRange.SelectedIndex = Ranges.Length - 1;

        _ready = true;
    }

    private TimeSpan? SelectedSpan => Ranges[Math.Max(0, CmbRange.SelectedIndex)].Span;

    /// <summary>
    /// Reads system_health. Called on navigate and by the sidebar Refresh; the read goes
    /// through every event file the session keeps, so it isn't repeated on a timer.
    /// </summary>
    public async Task RefreshAsync()
    {
        BtnReload.IsEnabled = false;
        TxtStatus.Text      = "Reading deadlock reports from system_health…";
        try
        {
            _history = await _repo.GetDeadlockHistoryAsync();
            Show();
        }
        finally
        {
            BtnReload.IsEnabled = true;
        }
    }

    private void Show()
    {
        if (_history is not { } history) return;

        var from     = SelectedSpan is { } span ? DateTime.UtcNow - span : DateTime.MinValue;
        var inRange  = history.Deadlocks.Where(d => d.TimestampUtc >= from).ToList();
        var (patterns, reports) = DeadlockPatterns.Group(inRange);

        var selected = DeadlocksGrid.SelectedItem as DeadlockReport;
        DataGridRefresh.SetItemsSource(DeadlocksGrid, reports);
        DataGridRefresh.SetItemsSource(PatternsGrid, patterns);

        // Keep the same deadlock selected when only the range changed; otherwise the newest.
        DeadlocksGrid.SelectedItem = reports.FirstOrDefault(r => selected is not null && SameDeadlock(r, selected))
                                     ?? reports.FirstOrDefault();

        TxtCount.Text = reports.Count switch
        {
            0     => "Deadlocks",
            1     => "1 deadlock",
            var n => $"{n:N0} deadlocks",
        };
        var recurring = patterns.Count(p => p.Count > 1);
        TxtPatternCount.Text = patterns.Count == 0 ? "Deadlock patterns"
            : $"{patterns.Count:N0} pattern{(patterns.Count == 1 ? "" : "s")}, {recurring:N0} seen more than once";

        TxtProblem.Text       = history.Problem ?? string.Empty;
        TxtProblem.Visibility = history.Problem is null ? Visibility.Collapsed : Visibility.Visible;
        TxtStatus.Text        = StatusText(history);

        TxtNoData.Text       = EmptyText(history, reports.Count);
        TxtNoData.Visibility = reports.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
            text.Append($" Oldest deadlock held: {history.Deadlocks[^1].Time:yyyy-MM-dd HH:mm}.");
        if (history.TotalFound > history.Deadlocks.Count)
            text.Append($" Showing the newest {history.Deadlocks.Count:N0} of {history.TotalFound:N0}.");
        if (history.Unreadable > 0)
            text.Append($" {history.Unreadable:N0} report{(history.Unreadable == 1 ? "" : "s")} could not be read.");
        return text.ToString();
    }

    private string EmptyText(DeadlockHistory history, int shown)
    {
        if (shown > 0) return string.Empty;
        if (history.Source == DeadlockSource.None) return history.Problem ?? "No deadlock history could be read.";
        if (history.Deadlocks.Count == 0) return "system_health holds no deadlock reports: no deadlock has happened in the time it covers.";
        return $"No deadlocks in the {Ranges[CmbRange.SelectedIndex].Label.ToLowerInvariant()}. " +
               $"system_health holds {history.Deadlocks.Count:N0} older: choose a longer range to see them.";
    }

    // ── Toolbar ───────────────────────────────────────────────────────────────

    private void CmbRange_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready) Show();
    }

    // The sidebar Refresh reports errors through MainWindow; a read started here reports its own.
    private async void BtnReload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Deadlocks: read failed", ex);
            TxtStatus.Text = MainWindow.FormatErrorStatus(ex);
        }
    }

    // ── Selected deadlock ─────────────────────────────────────────────────────

    private DeadlockReport? Selected => DeadlocksGrid.SelectedItem as DeadlockReport;

    private void DeadlocksGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Selected is not { } report)
        {
            DetailPanel.IsEnabled    = false;
            TxtDetailHeader.Text     = "Select a deadlock above to see who took part.";
            ProcessesGrid.ItemsSource = null;
            ResourcesGrid.ItemsSource = null;
            TxtStatement.Text        = string.Empty;
            return;
        }

        DetailPanel.IsEnabled = true;
        TxtDetailHeader.Text  = $"Deadlock at {report.Time:yyyy-MM-dd HH:mm:ss.fff} · {report.SessionCount} session" +
                                $"{(report.SessionCount == 1 ? " (parallel query)" : "s")} · {report.Resources.Count} resource" +
                                $"{(report.Resources.Count == 1 ? "" : "s")}";

        // Victims first: the session the application saw fail.
        var processes = report.Processes.OrderByDescending(p => p.IsVictim).ToList();
        ProcessesGrid.ItemsSource  = processes;
        ResourcesGrid.ItemsSource  = report.Resources;
        ProcessesGrid.SelectedItem = processes.FirstOrDefault();
    }

    private void ProcessesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        TxtStatement.Text = ProcessesGrid.SelectedItem is DeadlockProcess p ? StatementText(p) : string.Empty;
    }

    private static string StatementText(DeadlockProcess p)
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

    private void BtnCopyXml_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is { } report)
            ClipboardHelper.SetText(report.Xml);
    }

    private void BtnSaveXdl_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } report) return;

        var dlg = new SaveFileDialog
        {
            Title    = "Save Deadlock Graph",
            Filter   = "Deadlock graph (*.xdl)|*.xdl|XML file (*.xml)|*.xml",
            FileName = $"Deadlock_{report.Time:yyyyMMdd_HHmmss}",
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            File.WriteAllText(dlg.FileName, report.Xml, Encoding.UTF8);
            MessageBox.Show(
                "Deadlock graph saved.\nOpen it in SSMS (File → Open → File…) to see the processes and resources drawn as a graph.",
                "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save the file:\n{ex.Message}",
                "Save Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── Recurring ─────────────────────────────────────────────────────────────

    private void OpenNewest_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: DeadlockPattern pattern })
            OpenNewest(pattern);
    }

    private void PatternsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PatternsGrid.SelectedItem is DeadlockPattern pattern)
            OpenNewest(pattern);
    }

    private void OpenNewest(DeadlockPattern pattern)
    {
        TabDeadlocks.IsSelected    = true;
        DeadlocksGrid.SelectedItem = pattern.Newest;

        // After the tab has laid the grid out, or there is nothing to scroll.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            if (DeadlocksGrid.SelectedItem is not null)
                DeadlocksGrid.ScrollIntoView(DeadlocksGrid.SelectedItem);
        });
    }
}
