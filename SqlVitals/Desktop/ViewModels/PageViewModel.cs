using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// The view model of a page (#183): its verdict on the <see cref="PageShell"/> template (#146), and
/// its lifecycle. The page factory makes it the page's <c>DataContext</c> and runs it: it loads when
/// the page is opened (<see cref="OnNavigatedToAsync"/>), reads again on Refresh or its
/// <see cref="RefreshInterval"/>, never twice at once, and cancels what it was reading when the page
/// is left (<see cref="OnNavigatedFrom"/>). A failure is reported once, to the top bar
/// (<see cref="IShellStatus"/>) and in the verdict.
/// </summary>
/// <remarks>
/// Each page's view model derives from it, one file each under <c>ViewModels/Pages</c> (#181). A page
/// whose code-behind still reads derives from <see cref="CodeBehindViewModel"/> until its wave of #180
/// moves the reading here.
/// </remarks>
public abstract partial class PageViewModel : ObservableObject, IRefreshable
{
    /// <summary>What the page says first; null until it has read something to judge.</summary>
    [ObservableProperty]
    private PageVerdict? _verdict;

    /// <summary>"Loading…", "Refreshing…", the last error as one line, or null when it read.</summary>
    [ObservableProperty]
    private string? _status;

    /// <summary>Reading now: Refresh waits for it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    private bool _isBusy;

    /// <summary>The timed refresh is paused (the top bar's Pause); Refresh still reads.</summary>
    [ObservableProperty]
    private bool _isPaused;

    /// <summary>
    /// When the timed refresh reads next, for a page that shows it (<see cref="ShowsCountdown"/>):
    /// "12s", "1:05", or "--" while nothing is timed. A page's countdown binds to it (#169).
    /// </summary>
    [ObservableProperty]
    private string _countdown = NoCountdown;

    private const string NoCountdown = "--";

    private IShellStatus? _shell;
    private IRefreshScheduler? _scheduler;
    private TimeSpan? _refreshInterval;
    private IRefreshSchedule? _schedule;
    private IRefreshSchedule? _countdownTick;
    private bool _showsCountdown;
    private IRefreshSchedule? _soon;
    private readonly List<(TimeSpan Every, Action Tick)> _viewTicks = [];
    private readonly List<IRefreshSchedule> _viewSchedules = [];
    private CancellationTokenSource? _run;
    private PageLifetime? _lifetime;
    private Task _reading = Task.CompletedTask;
    private bool _shown;

    protected PageViewModel() =>
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);

    /// <summary>Reads again, as the top bar's Refresh and F5 do. While a read runs, it waits for that one instead.</summary>
    public IAsyncRelayCommand RefreshCommand { get; }

    /// <summary>The page's tag in <see cref="PageCatalog"/>, for the log.</summary>
    public string Tag { get; private set; } = string.Empty;

    /// <summary>
    /// Hands the view model what runs its lifecycle; the page factory calls it as it makes the page.
    /// Until then (a page built on its own in a test) nothing is reported and nothing is timed.
    /// </summary>
    /// <param name="lifetime">The page's reads end with it (#167): ended when the page is left.</param>
    internal void Connect(string tag, IShellStatus shell, IRefreshScheduler scheduler, PageLifetime? lifetime = null)
    {
        Tag        = tag;
        _shell     = shell;
        _scheduler = scheduler;
        _lifetime  = lifetime;
    }

    /// <summary>
    /// How often the page reads on its own while it is shown and not paused; null for a page that
    /// reads only when opened and on Refresh. Changing it restarts the wait.
    /// </summary>
    protected TimeSpan? RefreshInterval
    {
        get => _refreshInterval;
        set
        {
            _refreshInterval = value;
            Reschedule();
        }
    }

    /// <summary>
    /// The page shows how long until its next timed read: <see cref="Countdown"/> then follows it
    /// every second while the timer runs.
    /// </summary>
    protected bool ShowsCountdown
    {
        get => _showsCountdown;
        set
        {
            _showsCountdown = value;
            Reschedule();
        }
    }

    /// <summary>Reads what the page shows. <paramref name="cancel"/> is cancelled when the page is left.</summary>
    protected abstract Task LoadAsync(CancellationToken cancel);

    /// <summary>
    /// Reads what the page shows, knowing why: a page whose timed read is narrower than its
    /// Refresh (#169) overrides this. <see cref="LoadAsync(CancellationToken)"/> otherwise.
    /// </summary>
    protected virtual Task LoadAsync(PageRead read, CancellationToken cancel) => LoadAsync(cancel);

    /// <summary>The page is on screen: it loads, then its timed refresh starts.</summary>
    public async Task OnNavigatedToAsync()
    {
        _shown = true;
        _lifetime?.Begin();
        StartViewTicks();
        await ReadAsync(PageRead.Load);
        Reschedule();
    }

    /// <summary>
    /// The page was left: what it was reading is cancelled, on the server too (#167), and its timer
    /// stops. Reads its code-behind started end with it.
    /// </summary>
    public void OnNavigatedFrom()
    {
        _shown = false;
        _run?.Cancel();
        _lifetime?.End();
        _soon?.Dispose();
        _soon = null;
        StartViewTicks();
        Reschedule();
    }

    /// <inheritdoc cref="RefreshCommand"/>
    [ReportsItsErrors]
    public Task RefreshAsync() => ReadAsync(PageRead.Refresh);

    /// <summary>
    /// What the page reads changed (a filter, a range): reads again, after the read in progress if
    /// there is one, since that one reads the old choice. Refresh would only wait for it.
    /// </summary>
    [ReportsItsErrors]
    public Task RereadAsync() => ReadAfterAsync(PageRead.Refresh);

    /// <summary>
    /// Reads once, <paramref name="delay"/> from now, as a timed read: after a change the page
    /// was told of that is saved moments later (an alert). Asked again meanwhile, the wait starts
    /// over, so a burst of changes reads once. Nothing while the page isn't shown.
    /// </summary>
    protected void RefreshSoon(TimeSpan delay)
    {
        _soon?.Dispose();
        _soon = _shown && _scheduler is not null ? _scheduler.Every(delay, ReadSoonAsync) : null;
    }

    private Task ReadSoonAsync()
    {
        _soon?.Dispose();
        _soon = null;
        return ReadAfterAsync(PageRead.Timed);
    }

    /// <summary>
    /// Calls <paramref name="tick"/> every <paramref name="every"/> while the page is shown, paused
    /// or not: for what the page draws from the clock (a "next try in" it works out itself), never
    /// to read. A page no longer needs a <c>DispatcherTimer</c> for it (#169).
    /// </summary>
    protected void WhileShownEvery(TimeSpan every, Action tick)
    {
        _viewTicks.Add((every, tick));
        StartViewTicks();
    }

    // The view ticks run while the page is shown.
    private void StartViewTicks()
    {
        foreach (var schedule in _viewSchedules)
            schedule.Dispose();
        _viewSchedules.Clear();
        if (!_shown || _scheduler is null)
            return;
        foreach (var (every, tick) in _viewTicks)
            _viewSchedules.Add(_scheduler.Every(every, () => { tick(); return Task.CompletedTask; }));
    }

    /// <summary>
    /// Awaits what one of the page's events started that isn't a read (a link, a plan), and reports
    /// what it throws as a failed read is: the top bar and the log (#169). An <c>async void</c>
    /// handler awaits it rather than <paramref name="work"/> itself; a cancellation says nothing.
    /// </summary>
    [ReportsItsErrors]
    public async Task ReportErrorsOf(Task work)
    {
        try
        {
            await work;
        }
        catch (OperationCanceledException)
        {
            // Left, or its server switched: nothing to say.
        }
        catch (Exception ex)
        {
            if (_shell is { } shell)
                shell.ReportError(this, Tag, ex, PageRead.Refresh);
            else
                AppLog.Error($"{Tag}: failed", ex);
        }
    }

    // A read that must see a change made during the one in progress: waits for it, then reads.
    private async Task ReadAfterAsync(PageRead read)
    {
        while (IsBusy)
            await _reading;
        await ReadAsync(read);
    }

    /// <summary>Pauses or resumes the timed refresh; resuming waits a whole interval again.</summary>
    public void SetPaused(bool paused)
    {
        IsPaused = paused;
        Reschedule();
    }

    /// <summary>
    /// What the page keeps when it is left, to be put back when its route is opened again (#139):
    /// a tab, a filter. Null keeps nothing; the grids' sort and scrolling are kept by the window.
    /// </summary>
    public virtual object? SaveState() => null;

    /// <summary>Puts back what <see cref="SaveState"/> kept, before the page loads.</summary>
    public virtual void RestoreState(object state) { }

    /// <summary>The page couldn't read: the verdict says so. A page whose code-behind says so itself does nothing here.</summary>
    protected virtual void ShowLoadError(Exception exception) => ShowError("this page's data", exception);

    /// <summary>
    /// The page couldn't read what it judges: the verdict says so, with the GRANT that fixes a
    /// missing permission (#167). A read cancelled because the page was left says nothing.
    /// </summary>
    /// <param name="what">"the sessions", "the backup history".</param>
    public void ShowError(string what, Exception error)
    {
        if (error is OperationCanceledException)
            return;
        Verdict = PageVerdict.Unreadable(what, ErrorStatus.Format(error), ErrorStatus.Fix(error));
    }

    // One read at a time: asked again while one runs, the caller waits for that one.
    private Task ReadAsync(PageRead read)
    {
        if (IsBusy)
            return _reading;
        var reading = RunAsync(read);
        if (!reading.IsCompleted)
            _reading = reading;
        return reading;
    }

    private async Task RunAsync(PageRead read)
    {
        using var run = new CancellationTokenSource();
        _run   = run;
        IsBusy = true;
        Status = read == PageRead.Load ? "Loading…" : read == PageRead.Refresh ? "Refreshing…" : Status;
        try
        {
            await LoadAsync(read, run.Token);
            if (!run.IsCancellationRequested)
            {
                Status = null;
                _shell?.ReportRead(this, read);
            }
        }
        catch (OperationCanceledException) when (run.IsCancellationRequested)
        {
            // Left while reading: nothing on screen is about it any more.
        }
        catch (Exception ex) when (run.IsCancellationRequested)
        {
            // Left, or its connection switched, while a read that couldn't be cancelled failed:
            // logged only, since it describes data no longer on screen.
            AppLog.Error($"{Tag}: a read the page had left failed", ex);
        }
        catch (Exception ex)
        {
            Status = ErrorStatus.Format(ex);
            ShowLoadError(ex);
            _shell?.ReportError(this, Tag, ex, read);
        }
        finally
        {
            if (ReferenceEquals(_run, run))
                _run = null;
            IsBusy = false;
        }
    }

    // The timer runs while the page is shown, has an interval and isn't paused; so does its countdown.
    private void Reschedule()
    {
        _schedule?.Dispose();
        _countdownTick?.Dispose();
        _schedule = _shown && !IsPaused && _refreshInterval is { } interval && _scheduler is not null
            ? _scheduler.Every(interval, () => ReadAsync(PageRead.Timed))
            : null;
        _countdownTick = _schedule is not null && _showsCountdown && _refreshInterval > Second
            ? _scheduler!.Every(Second, ShowCountdownAsync)
            : null;
        Countdown = _schedule is null ? NoCountdown : CountdownText(_schedule.Remaining);
    }

    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private Task ShowCountdownAsync()
    {
        if (_schedule is { } schedule)
            Countdown = CountdownText(schedule.Remaining);
        return Task.CompletedTask;
    }

    /// <summary>"12s" under a minute, "1:05" from one; whole seconds, rounded up, so it reaches 0s as it reads.</summary>
    internal static string CountdownText(TimeSpan left)
    {
        var seconds = (int)Math.Ceiling(Math.Max(0, left.TotalSeconds));
        return seconds >= 60 ? $"{seconds / 60}:{seconds % 60:00}" : $"{seconds}s";
    }

    // Concurrent: a press while another page opens isn't held back, as before the actions could await.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task Act(VerdictAction action) => OnActionAsync(action);

    /// <summary>Puts the verdict, and what its actions do, on the page's shell.</summary>
    public void Attach(PageShell shell)
    {
        shell.SetBinding(PageShell.VerdictProperty, new Binding(nameof(Verdict)) { Source = this });
        shell.VerdictCommand = ActCommand;
    }

    /// <summary>One of the verdict's actions was pressed: one that opens another page (<see cref="INavigator"/>).</summary>
    protected virtual Task OnActionAsync(VerdictAction action)
    {
        OnAction(action);
        return Task.CompletedTask;
    }

    /// <summary>One of the verdict's actions was pressed: one that stays on the page.</summary>
    protected virtual void OnAction(VerdictAction action) { }
}
