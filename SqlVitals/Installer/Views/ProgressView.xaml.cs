using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlVitals.Installer.Core;

namespace SqlVitals.Installer.Views;

public partial class ProgressView : UserControl
{
    private readonly List<(TextBlock Icon, TextBlock Label)> _rows = new();
    private IReadOnlyList<IInstallStep> _steps = Array.Empty<IInstallStep>();
    private TaskCompletionSource<bool>? _decision;
    private bool _uninstall;

    public ProgressView() => InitializeComponent();

    /// <summary>Raised when the user gives up after a failure; the shell confirms and cancels the run.</summary>
    public event EventHandler? CancelRequested;

    public bool IsRollingBack { get; private set; }

    public void Begin(SetupSession session, IReadOnlyList<IInstallStep> steps)
    {
        _steps     = steps;
        _uninstall = session.Mode == InstallMode.Uninstall;
        IsRollingBack = false;
        _decision  = null;

        Title.Text = session.Mode switch
        {
            InstallMode.Upgrade   => "Upgrading SqlVitals",
            InstallMode.Repair    => "Repairing SqlVitals",
            InstallMode.Downgrade => "Replacing SqlVitals",
            InstallMode.Uninstall => "Uninstalling SqlVitals",
            _                     => "Installing SqlVitals",
        };
        Status.Text          = "Starting…";
        BtnGiveUp.Content    = _uninstall ? "Stop uninstalling" : "Cancel and undo changes";
        ErrorPanel.Visibility = Visibility.Collapsed;
        SetProgress(0);

        _rows.Clear();
        StepList.Children.Clear();
        foreach (var step in steps)
        {
            var row   = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            var icon  = new TextBlock { Width = 24, Text = "○", Foreground = Brush("TextMuted"), FontWeight = FontWeights.Bold };
            var label = new TextBlock { Text = step.Title, Foreground = Brush("TextMuted") };
            row.Children.Add(icon);
            row.Children.Add(label);
            StepList.Children.Add(row);
            _rows.Add((icon, label));
        }
    }

    public void MarkStarted(int index)
    {
        ErrorPanel.Visibility = Visibility.Collapsed;
        Status.Text = _steps[index].Title + "…";
        var (icon, label) = _rows[index];
        icon.Text        = "›";
        icon.Foreground  = Brush("Accent");
        label.Foreground = Brush("TextPrimary");
        label.FontWeight = FontWeights.SemiBold;
    }

    public void MarkCompleted(int index)
    {
        var (icon, label) = _rows[index];
        icon.Text        = "✓";
        icon.Foreground  = Brush("Success");
        label.FontWeight = FontWeights.Normal;
    }

    public void SetProgress(double fraction)
    {
        Bar.Value    = fraction;
        Percent.Text = $"{Math.Round(fraction * 100):0}%";
    }

    /// <summary>Shows the failure and waits for the user: true = retry, false = cancel.</summary>
    public Task<bool> AskRetryAsync(StepFailure failure)
    {
        var index = IndexOf(failure.StepTitle);
        if (index >= 0)
        {
            var (icon, label) = _rows[index];
            icon.Text       = "✕";
            icon.Foreground = Brush("Danger");
            label.Foreground = Brush("Danger");
        }

        Status.Text             = "Setup stopped because a step failed.";
        ErrorTitle.Text         = $"Couldn't finish: {failure.StepTitle}";
        ErrorMessage.Text       = failure.Message;
        ErrorResolution.Text    = failure.Resolution;
        ErrorPanel.Visibility   = Visibility.Visible;
        BtnRetry.Focus();

        _decision = new TaskCompletionSource<bool>();
        return _decision.Task;
    }

    public void ShowCancelling()
    {
        Status.Text = _uninstall ? "Stopping…" : "Cancelling — undoing changes…";
        Resolve(false);
    }

    public void ShowRollingBack()
    {
        IsRollingBack = true;
        ErrorPanel.Visibility = Visibility.Collapsed;
        Status.Text = _uninstall ? "Stopping…" : "Undoing changes…";
    }

    private void Retry_Click(object sender, RoutedEventArgs e)
    {
        ErrorPanel.Visibility = Visibility.Collapsed;
        Status.Text = "Retrying…";
        Resolve(true);
    }

    private void GiveUp_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(SetupLog.Path))
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{SetupLog.Path}\"") { UseShellExecute = true });
    }

    private void Resolve(bool retry)
    {
        var decision = _decision;
        _decision = null;
        decision?.TrySetResult(retry);
    }

    private int IndexOf(string title)
    {
        for (var i = 0; i < _steps.Count; i++)
            if (_steps[i].Title == title) return i;
        return -1;
    }

    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}
