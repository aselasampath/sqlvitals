using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SqlVitals.Installer.Core;
using SqlVitals.Installer.Views;

namespace SqlVitals.Installer;

/// <summary>
/// Wizard shell: sidebar of steps, one page at a time, Back / Next / Cancel footer.
/// Install:   Welcome → License → System check → Options → Review → Installing → Finish
/// Uninstall: (Welcome →) Confirm → Uninstalling → Finish
/// </summary>
public partial class MainWindow : Window
{
    private enum Page { Welcome, License, Requirements, Options, Review, ConfirmUninstall, Progress, Finish }

    private readonly SetupSession     _session;
    private readonly WelcomeView      _welcome;
    private readonly LicenseView      _license;
    private readonly RequirementsView _requirements;
    private readonly OptionsView      _options;
    private readonly ReviewView       _review;
    private readonly UninstallView    _uninstall;
    private readonly ProgressView     _progress;
    private readonly FinishView       _finish;

    private Page _page;
    private CancellationTokenSource? _runCts;
    private bool _running;
    private bool _allowClose;

    public MainWindow(SetupSession session)
    {
        InitializeComponent();
        _session = session;

        _welcome      = new WelcomeView(session);
        _license      = new LicenseView();
        _requirements = new RequirementsView(session);
        _options      = new OptionsView(session);
        _review       = new ReviewView(session);
        _uninstall    = new UninstallView(session);
        _progress     = new ProgressView();
        _finish       = new FinishView(session);

        _requirements.StateChanged += (_, _) => UpdateButtons();
        _requirements.RestartElevatedRequested += (_, _) => RestartElevated();
        _options.RestartElevatedRequested      += (_, _) => RestartElevated(App.InstallDirArg + _options.EnteredLocation);
        _uninstall.RestartElevatedRequested    += (_, _) => RestartElevated(App.UninstallArg);
        _progress.CancelRequested              += (_, _) => RequestCancelRun();

        SidebarSubtitle.Text = $"Setup · version {session.PackageVersionText}";
        SidebarFooter.Text   = Elevation.IsElevated ? "Running as administrator" : string.Empty;

        Go(session.StartWithUninstall ? Page.ConfirmUninstall : Page.Welcome);
    }

    // ── Navigation ────────────────────────────────────────────────────────────

    private void Go(Page page)
    {
        _page = page;
        PageHost.Content = page switch
        {
            Page.Welcome          => _welcome,
            Page.License          => _license,
            Page.Requirements     => _requirements,
            Page.Options          => _options,
            Page.Review           => _review,
            Page.ConfirmUninstall => _uninstall,
            Page.Progress         => _progress,
            _                     => (UserControl)_finish,
        };

        switch (page)
        {
            case Page.Requirements: _ = _requirements.RunChecksAsync(); break;
            case Page.Options:      _options.Load();                   break;
            case Page.Review:       _review.Load();                    break;
            case Page.ConfirmUninstall: _uninstall.Load();             break;
        }

        RenderSidebar();
        UpdateButtons();
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        switch (_page)
        {
            case Page.Welcome:
                _session.Mode = _welcome.SelectedMode;
                Go(_session.Mode == InstallMode.Uninstall ? Page.ConfirmUninstall : Page.License);
                break;
            case Page.License:
                Go(Page.Requirements);
                break;
            case Page.Requirements:
                Go(Page.Options);
                break;
            case Page.Options:
                if (_options.TryApply()) Go(Page.Review);
                break;
            case Page.Review:
            case Page.ConfirmUninstall:
                _ = RunAsync();
                break;
            case Page.Finish:
                _allowClose = true;
                Close();
                break;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        switch (_page)
        {
            case Page.License:          Go(Page.Welcome);      break;
            case Page.Requirements:     Go(Page.License);      break;
            case Page.Options:          Go(Page.Requirements); break;
            case Page.Review:           Go(Page.Options);      break;
            case Page.ConfirmUninstall: Go(Page.Welcome);      break;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_allowClose || _page == Page.Finish) return;

        e.Cancel = true;
        if (_running)
        {
            RequestCancelRun();
            return;
        }

        var what = _session.Mode == InstallMode.Uninstall ? "SqlVitals hasn't been uninstalled." : "Nothing has been changed on your PC.";
        if (MessageBox.Show(this, $"Exit SqlVitals Setup?\n\n{what}", "SqlVitals Setup",
                            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
        {
            _allowClose = true;
            Dispatcher.BeginInvoke(new Action(Close));
        }
    }

    // ── Install / uninstall ───────────────────────────────────────────────────

    private async System.Threading.Tasks.Task RunAsync()
    {
        var uninstall = _session.Mode == InstallMode.Uninstall;
        if (uninstall)
            _session.RemoveUserData = _uninstall.RemoveUserData;

        var context = _session.CreateContext();
        var steps   = uninstall ? InstallSteps.ForUninstall() : InstallSteps.ForInstall();
        SetupLog.Info($"{_session.Mode}: {context.InstallDir} ({context.Scope}), desktop shortcut: {context.CreateDesktopShortcut}");

        _runCts  = new CancellationTokenSource();
        _running = true;
        _progress.Begin(_session, steps);
        Go(Page.Progress);

        var runner = new InstallRunner();
        runner.StepStarted     += _progress.MarkStarted;
        runner.StepCompleted   += _progress.MarkCompleted;
        runner.ProgressChanged += _progress.SetProgress;
        runner.RollingBack     += () => Dispatcher.Invoke(() => { _progress.ShowRollingBack(); UpdateButtons(); });

        InstallOutcome outcome;
        try
        {
            outcome = await runner.RunAsync(steps, context, _progress.AskRetryAsync, _runCts.Token);
        }
        finally
        {
            _running = false;
            _runCts.Dispose();
            _runCts = null;
        }

        SetupLog.Info($"Outcome: {outcome}");
        _finish.Show(outcome, context);
        Go(Page.Finish);
    }

    private void RequestCancelRun()
    {
        if (!_running || _runCts is null || _runCts.IsCancellationRequested) return;

        var question = _session.Mode == InstallMode.Uninstall
            ? "Stop uninstalling?\n\nFiles already removed stay removed. You can run Uninstall again later to finish."
            : "Cancel the installation?\n\nSetup will undo the changes it has made so far" +
              (_session.Existing is null ? "." : " and restore your previous SqlVitals installation.");

        if (MessageBox.Show(this, question, "SqlVitals Setup", MessageBoxButton.YesNo,
                            MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;

        _runCts.Cancel();
        _progress.ShowCancelling();
        UpdateButtons();
    }

    /// <param name="extraArg">Carries the user's choice over, so the elevated copy picks up where they were.</param>
    private void RestartElevated(string? extraArg = null)
    {
        var args = _session.Args.Where(a => !a.StartsWith(App.InstallDirArg, StringComparison.OrdinalIgnoreCase)).ToList();
        if (extraArg is not null && !args.Contains(extraArg, StringComparer.OrdinalIgnoreCase))
            args.Add(extraArg);

        _allowClose = true;
        if (((App)Application.Current).RestartElevated(args.ToArray()))
            return;

        _allowClose = false;
        MessageBox.Show(this,
            "Setup needs administrator rights for this, and the Windows prompt was declined.\n\n" +
            "You can continue without them by installing to a folder in your user profile (the default).",
            "SqlVitals Setup", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ── Chrome ────────────────────────────────────────────────────────────────

    private void UpdateButtons()
    {
        BtnBack.Visibility   = Visibility.Visible;
        BtnNext.Visibility   = Visibility.Visible;
        BtnCancel.Visibility = Visibility.Visible;
        BtnBack.IsEnabled    = true;
        BtnNext.IsEnabled    = true;
        BtnCancel.IsEnabled  = true;
        BtnCancel.Content    = "Cancel";

        switch (_page)
        {
            case Page.Welcome:
                BtnBack.Visibility = Visibility.Collapsed;
                BtnNext.Content    = _session.Existing is null ? "Begin installation" : "Continue";
                BtnNext.IsEnabled  = _welcome.CanContinue;
                break;
            case Page.License:
                BtnNext.Content = "I agree";
                break;
            case Page.Requirements:
                BtnNext.Content   = "Next";
                BtnNext.IsEnabled = _requirements.CanContinue;
                break;
            case Page.Options:
                BtnNext.Content = "Next";
                break;
            case Page.Review:
                BtnNext.Content = _session.ActionVerb;
                break;
            case Page.ConfirmUninstall:
                BtnBack.Visibility = _session.StartWithUninstall ? Visibility.Collapsed : Visibility.Visible;
                BtnNext.Content    = "Uninstall";
                BtnNext.IsEnabled  = _uninstall.CanUninstall;
                break;
            case Page.Progress:
                BtnBack.Visibility = Visibility.Collapsed;
                BtnNext.Visibility = Visibility.Collapsed;
                BtnCancel.IsEnabled = _runCts is { IsCancellationRequested: false } && !_progress.IsRollingBack;
                break;
            case Page.Finish:
                BtnBack.Visibility   = Visibility.Collapsed;
                BtnCancel.Visibility = Visibility.Collapsed;
                BtnNext.Content      = "Finish";
                break;
        }
    }

    private void RenderSidebar()
    {
        var uninstall = _session.Mode == InstallMode.Uninstall;
        var titles = uninstall
            ? new[] { "Welcome", "Confirm", "Uninstall", "Finish" }
            : new[] { "Welcome", "License", "System check", "Options", "Review", "Install", "Finish" };
        var current = uninstall
            ? _page switch { Page.Welcome => 0, Page.ConfirmUninstall => 1, Page.Progress => 2, _ => 3 }
            : _page switch { Page.Welcome => 0, Page.License => 1, Page.Requirements => 2, Page.Options => 3, Page.Review => 4, Page.Progress => 5, _ => 6 };
        if (uninstall && _session.StartWithUninstall)
        {
            titles  = new[] { "Confirm", "Uninstall", "Finish" };
            current = Math.Max(0, current - 1);
        }

        StepList.Children.Clear();
        for (var i = 0; i < titles.Length; i++)
        {
            var done   = i < current;
            var active = i == current;
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            row.Children.Add(new Border
            {
                Width = 22, Height = 22, CornerRadius = new CornerRadius(11),
                Background = (Brush)FindResource(active ? "Accent" : done ? "SuccessSoft" : "BgCard"),
                BorderBrush = (Brush)FindResource(done ? "Success" : active ? "Accent" : "Border"),
                BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = done ? "✓" : (i + 1).ToString(),
                    FontSize = 11, FontWeight = FontWeights.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = active ? Brushes.White : (Brush)FindResource(done ? "Success" : "TextMuted"),
                },
            });
            row.Children.Add(new TextBlock
            {
                Text = titles[i], Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = (Brush)FindResource(active ? "TextPrimary" : "TextMuted"),
            });
            StepList.Children.Add(row);
        }
    }
}
