using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

public partial class ProcessesPage : Page, IRefreshable
{
    private readonly IWaitStatsRepository _repo;

    // ?? Auto-refresh ??????????????????????????????????????????????????
    private readonly DispatcherTimer _timer = new();
    private int _intervalSec   = 10;   // default selection
    private int _remainingSec  = 0;
    private bool _refreshing   = false;

    // Interval choices: 5 s increments up to 120 s
    private static readonly int[] Intervals =
        Enumerable.Range(1, 24).Select(i => i * 5).ToArray(); // 5,10,15,...,120

    public ProcessesPage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();

        foreach (var sec in Intervals)
            CmbInterval.Items.Add(new ComboBoxItem
            {
                Content = sec < 60 ? $"{sec}s" : $"{sec / 60}m {sec % 60:00}s",
                Tag     = sec
            });
        CmbInterval.SelectedIndex = 1; // default 10 s

        // Wire timer (ticks every second for the countdown display)
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick    += Timer_Tick;

        // Stop timer when page is unloaded (navigation away)
        Unloaded += (_, _) => StopTimer();
    }

    public async System.Threading.Tasks.Task RefreshAsync()
    {
        ProcessMap.UpdateNodes(await _repo.GetProcessesAsync());
    }

    // ?? Timer logic ???????????????????????????????????????????????????
    private void StartTimer()
    {
        _remainingSec = _intervalSec;
        UpdateCountdown();
        _timer.Start();
    }

    private void StopTimer()
    {
        _timer.Stop();
        TxtCountdown.Text = "--";
        if (BtnAutoRefresh.IsChecked == true)
            BtnAutoRefresh.IsChecked = false;
    }

    private async void Timer_Tick(object? sender, EventArgs e)
    {
        _remainingSec--;
        UpdateCountdown();

        if (_remainingSec > 0 || _refreshing) return;

        _refreshing = true;
        try
        {
            await RefreshAsync();
            MainWindow.ReportBackgroundSuccess(this);
        }
        catch (Exception ex)
        {
            MainWindow.ReportBackgroundError(this, "Processes", ex);
        }
        finally
        {
            _refreshing   = false;
            _remainingSec = _intervalSec;   // reset countdown
            UpdateCountdown();
        }
    }

    private void UpdateCountdown()
    {
        TxtCountdown.Text = _remainingSec >= 60
            ? $"{_remainingSec / 60}:{_remainingSec % 60:00}"
            : $"{_remainingSec}s";
    }

    // ?? UI event handlers ?????????????????????????????????????????????
    private void CmbInterval_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbInterval.SelectedItem is ComboBoxItem item && item.Tag is int sec)
        {
            _intervalSec  = sec;
            _remainingSec = sec;
            UpdateCountdown();
        }
    }

    private void BtnAutoRefresh_Checked(object sender, RoutedEventArgs e)
    {
        BtnAutoRefresh.Content = "Stop";
        StartTimer();
    }

    private void BtnAutoRefresh_Unchecked(object sender, RoutedEventArgs e)
    {
        BtnAutoRefresh.Content = "Start";
        _timer.Stop();
        TxtCountdown.Text = "--";
    }
}
