using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

public partial class ProcessesPage : Page, IRefreshable
{
    private readonly IWaitStatsRepository _repo;
    private readonly ConnectionSettingsService _settings;

    // ?? Auto-refresh ??????????????????????????????????????????????????
    private readonly DispatcherTimer _timer = new();
    private int _intervalSec   = ConnectionStore.DefaultProcessesRefreshSeconds;
    private int _remainingSec  = 0;
    private bool _refreshing   = false;
    private bool _restoring    = true;   // no saving while the saved choice is applied, or while leaving

    // Interval choices: 5 s increments up to 120 s
    private static readonly int[] Intervals =
        Enumerable.Range(1, 24).Select(i => i * 5).ToArray(); // 5,10,15,...,120

    public ProcessesPage(IWaitStatsRepository repo, ConnectionSettingsService settings)
    {
        _repo     = repo;
        _settings = settings;
        InitializeComponent();

        foreach (var sec in Intervals)
            CmbInterval.Items.Add(new ComboBoxItem
            {
                Content = sec < 60 ? $"{sec}s" : $"{sec / 60}m {sec % 60:00}s",
                Tag     = sec
            });

        // The interval and the Start/Stop choice are remembered (#37).
        var store = settings.Load();
        CmbInterval.SelectedIndex = Math.Max(0, Array.IndexOf(Intervals, store.ProcessesRefreshSeconds));

        // Wire timer (ticks every second for the countdown display)
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick    += Timer_Tick;

        Loaded += (_, _) =>
        {
            if (store.ProcessesAutoRefresh) BtnAutoRefresh.IsChecked = true;
            _restoring = false;
        };

        // Stop timer when page is unloaded (navigation away), keeping the saved choice
        Unloaded += (_, _) =>
        {
            _restoring = true;
            StopTimer();
        };
    }

    private void SaveRefreshChoice()
    {
        if (_restoring) return;
        try
        {
            _settings.SetProcessesRefresh(_intervalSec, BtnAutoRefresh.IsChecked == true);
        }
        catch (Exception ex)
        {
            AppLog.Error("Processes: saving the auto-refresh choice failed", ex);
        }
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
            SaveRefreshChoice();
        }
    }

    private void BtnAutoRefresh_Checked(object sender, RoutedEventArgs e)
    {
        BtnAutoRefresh.Content = "Stop";
        StartTimer();
        SaveRefreshChoice();
    }

    private void BtnAutoRefresh_Unchecked(object sender, RoutedEventArgs e)
    {
        BtnAutoRefresh.Content = "Start";
        _timer.Stop();
        TxtCountdown.Text = "--";
        SaveRefreshChoice();
    }
}
