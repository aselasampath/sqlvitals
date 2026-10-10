using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.History;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// The activity log (#115), newest first: when monitoring started and stopped, the app started
/// and exited, settings and connections changed, and history was cleared. Filtered by connection,
/// event and period; read from history.db off the UI thread.
/// </summary>
public partial class ActivityLogView : UserControl
{
    private sealed record Period(string Label, TimeSpan Span);

    private static readonly IReadOnlyList<Period> Periods =
    [
        new("Last 24 hours", TimeSpan.FromDays(1)),
        new("Last 7 days",   TimeSpan.FromDays(7)),
        new("Last 30 days",  TimeSpan.FromDays(30)),
        new("Last 365 days", ActivityLog.Retention),
    ];

    private IHistoryReader? _reader;
    private bool           _ready;
    private int            _loadVersion;   // a slower, earlier load's result is dropped

    public ActivityLogView()
    {
        InitializeComponent();
    }

    /// <summary>Fills the filters and reads the log. The connections are the saved ones, by name.</summary>
    public void Initialize(IHistoryReader reader, IEnumerable<(Guid Id, string Name)> connections)
    {
        _reader = reader;
        _ready  = false;

        CmbConnection.Items.Clear();
        CmbConnection.Items.Add(new ComboBoxItem { Content = "All", Tag = null });
        foreach (var (id, name) in connections.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase))
            CmbConnection.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        CmbConnection.SelectedIndex = 0;

        CmbEvent.Items.Clear();
        CmbEvent.Items.Add(new ComboBoxItem { Content = "All events", Tag = null });
        foreach (var evt in ActivityEvents.All)
            CmbEvent.Items.Add(new ComboBoxItem { Content = evt, Tag = evt });
        CmbEvent.SelectedIndex = 0;

        CmbPeriod.Items.Clear();
        foreach (var period in Periods)
            CmbPeriod.Items.Add(new ComboBoxItem { Content = period.Label, Tag = period });
        CmbPeriod.SelectedIndex = 1;

        _ready = true;
        _ = RefreshQuietlyAsync();
    }

    /// <summary>Reads the log again with the filters chosen. Throws when the file can't be read.</summary>
    public async Task RefreshAsync()
    {
        if (_reader is not { } reader)
            return;

        var version      = ++_loadVersion;
        var connectionId = (CmbConnection.SelectedItem as ComboBoxItem)?.Tag as Guid?;
        var evt          = (CmbEvent.SelectedItem as ComboBoxItem)?.Tag as string;
        var period       = (Period)((ComboBoxItem)CmbPeriod.SelectedItem).Tag;
        var nowUtc       = DateTime.UtcNow;

        IReadOnlyList<StoredActivity> rows;
        string? problem = null;
        try
        {
            // A minute ahead, so a row logged a moment ago is in range whatever the rounding.
            rows = await Task.Run(() => reader.ReadActivity(nowUtc - period.Span, nowUtc.AddMinutes(1), connectionId, evt));
        }
        catch (HistorySchemaTooNewException ex)
        {
            rows    = [];
            problem = ex.Message;
        }
        if (version != _loadVersion)
            return;

        DataGridRefresh.SetItemsSource(ActivityGrid, rows.Select(r => new ActivityRow(r.Entry)).ToList());
        BtnExport.IsEnabled = rows.Count > 0;

        var culture = CultureInfo.CurrentCulture;
        TxtCount.Text = (rows.Count >= HistoryReader.MaxActivityRows
                            ? string.Format(culture, "The newest {0:N0} rows. Narrow the filters to see older ones. ", rows.Count)
                            : string.Format(culture, "{0:N0} row{1}. ", rows.Count, rows.Count == 1 ? "" : "s"))
                        + $"Times are on this PC's clock. Kept for {ActivityLog.RetentionDays} days, whatever the history retention; " +
                          "Clear history doesn't delete it.";

        TxtNoData.Text       = problem ?? "Nothing logged for these filters. Monitoring starting and stopping, " +
                                          "SqlVitals starting and exiting, and every settings change are logged here.";
        TxtNoData.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Reads the log again, showing a failure in the view rather than throwing.</summary>
    [ReportsItsErrors]
    public async Task RefreshQuietlyAsync()
    {
        try
        {
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            AppLog.Error("Activity log: load failed", ex);
            TxtCount.Text = $"Could not read the activity log: {ex.Message}";
        }
    }

    private async void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready)
            await RefreshQuietlyAsync();
    }

    private async void BtnRefresh_Click(object sender, RoutedEventArgs e) => await RefreshQuietlyAsync();

    private void BtnExport_Click(object sender, RoutedEventArgs e) => DataGridExport.ExportCsv(ActivityGrid);

    /// <summary>One row as the grid shows it; the time on this PC's clock.</summary>
    public sealed class ActivityRow(ActivityEntry entry)
    {
        public DateTime Time           { get; } = entry.TimeUtc.ToLocalTime();
        public string   Event          => entry.Event;
        public string   ConnectionName => entry.ConnectionName ?? "";
        public string   Source         => entry.Source;
        public string   Setting        => entry.Setting ?? "";
        public string   OldValue       => entry.OldValue ?? "";
        public string   NewValue       => entry.NewValue ?? "";
        public string   Detail         => entry.Detail ?? "";
        public string   WindowsUser    => entry.WindowsUser;
        public string   Machine        => entry.Machine;
    }
}
