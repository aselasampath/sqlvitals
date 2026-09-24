using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SqlVitals.Engine.History;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// Live / 1h / 24h / 7d / Custom switch shared by the trend pages, and the "Compare to baseline"
/// toggle. Anything but Live, and the baseline, is read from the monitoring history, so those
/// are disabled when there is none to read.
/// </summary>
public partial class TimeRangePicker : UserControl
{
    private HistoryRange _range = HistoryRange.Live;

    public TimeRangePicker()
    {
        InitializeComponent();
        Unloaded += (_, _) => CustomPopup.IsOpen = false;
    }

    /// <summary>Raised when the user picks a different range, or picks the same custom one again.</summary>
    public event EventHandler<HistoryRange>? RangeChanged;

    public HistoryRange Range => _range;

    /// <summary>Raised when "Compare to baseline" is turned on or off.</summary>
    public event EventHandler<bool>? BaselineChanged;

    public bool CompareToBaseline => BtnBaseline.IsChecked == true;

    /// <summary>
    /// Turns the history choices and the baseline off, with the reason as their tooltip, or back
    /// on. Going unavailable while showing history switches back to Live, without a baseline.
    /// </summary>
    public void SetHistoryAvailable(bool available, string? reason = null)
    {
        foreach (var button in HistoryButtons)
        {
            button.IsEnabled = available;
            ToolTipService.SetShowOnDisabled(button, true);
            if (!available)
                button.ToolTip = reason;
        }
        if (available)
        {
            BtnHour.ToolTip     = "The last hour, from the monitoring history";
            BtnDay.ToolTip      = "The last 24 hours, from the monitoring history";
            BtnWeek.ToolTip     = "The last 7 days, from the monitoring history";
            BtnCustom.ToolTip   = "Pick a start and end from the monitoring history";
            BtnBaseline.ToolTip = "Overlay the same time last week as dashed lines, to see whether now is unusual";
        }
        else
        {
            BtnBaseline.IsChecked = false;
            if (!_range.IsLive)
                Select(HistoryRange.Live);
        }
    }

    private ButtonBase[] HistoryButtons => [BtnHour, BtnDay, BtnWeek, BtnCustom, BtnBaseline];

    private void Baseline_Changed(object sender, RoutedEventArgs e) =>
        BaselineChanged?.Invoke(this, CompareToBaseline);

    private void Preset_Click(object sender, RoutedEventArgs e)
    {
        CustomPopup.IsOpen = false;
        var range = sender == BtnHour ? HistoryRange.LastHour
                  : sender == BtnDay  ? HistoryRange.Last24Hours
                  : sender == BtnWeek ? HistoryRange.Last7Days
                  : HistoryRange.Live;
        if (range != _range)
            Select(range);
    }

    private void Custom_Click(object sender, RoutedEventArgs e)
    {
        // The button stays on the current range until a custom one is shown.
        SyncButtons();

        if (CustomPopup.IsOpen)
        {
            CustomPopup.IsOpen = false;
            return;
        }

        // Start from the custom range on screen, or else the last hour.
        var (fromUtc, toUtc) = _range.Kind == HistoryRangeKind.Custom
            ? _range.Resolve(DateTime.UtcNow)
            : HistoryRange.LastHour.Resolve(DateTime.UtcNow);
        var from = fromUtc.ToLocalTime();
        var to   = toUtc.ToLocalTime();

        FromDate.SelectedDate = from.Date;
        FromTime.Text         = from.ToString("HH:mm");
        ToDate.SelectedDate   = to.Date;
        ToTime.Text           = to.ToString("HH:mm");
        TxtError.Visibility   = Visibility.Collapsed;
        CustomPopup.IsOpen    = true;
        FromTime.Focus();
    }

    private void CustomApply_Click(object sender, RoutedEventArgs e) => ApplyCustom();

    private void CustomCancel_Click(object sender, RoutedEventArgs e) => CustomPopup.IsOpen = false;

    private void CustomPopup_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            CustomPopup.IsOpen = false;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            ApplyCustom();
            e.Handled = true;
        }
    }

    private void CustomPopup_Closed(object? sender, EventArgs e) => SyncButtons();

    private void ApplyCustom()
    {
        if (!HistoryRange.TryParseCustom(FromDate.SelectedDate, FromTime.Text, ToDate.SelectedDate, ToTime.Text,
                                         DateTime.UtcNow, out var range, out var error))
        {
            TxtError.Text       = error;
            TxtError.Visibility = Visibility.Visible;
            return;
        }

        CustomPopup.IsOpen = false;
        Select(range!);
    }

    private void Select(HistoryRange range)
    {
        _range = range;
        SyncButtons();
        BtnCustom.Content = range.Kind == HistoryRangeKind.Custom ? range.Label : "Custom…";
        RangeChanged?.Invoke(this, range);
    }

    private void SyncButtons()
    {
        BtnLive.IsChecked   = _range.Kind == HistoryRangeKind.Live;
        BtnHour.IsChecked   = _range.Kind == HistoryRangeKind.LastHour;
        BtnDay.IsChecked    = _range.Kind == HistoryRangeKind.Last24Hours;
        BtnWeek.IsChecked   = _range.Kind == HistoryRangeKind.Last7Days;
        BtnCustom.IsChecked = _range.Kind == HistoryRangeKind.Custom;
    }
}
