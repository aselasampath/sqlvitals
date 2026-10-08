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

    private IShellStatus? _shell;
    private IRefreshScheduler? _scheduler;
    private TimeSpan? _refreshInterval;
    private IDisposable? _schedule;
    private CancellationTokenSource? _run;
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
    internal void Connect(string tag, IShellStatus shell, IRefreshScheduler scheduler)
    {
        Tag        = tag;
        _shell     = shell;
        _scheduler = scheduler;
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

    /// <summary>Reads what the page shows. <paramref name="cancel"/> is cancelled when the page is left.</summary>
    protected abstract Task LoadAsync(CancellationToken cancel);

    /// <summary>The page is on screen: it loads, then its timed refresh starts.</summary>
    public async Task OnNavigatedToAsync()
    {
        _shown = true;
        await ReadAsync(PageRead.Load);
        Reschedule();
    }

    /// <summary>The page was left: what it was reading is cancelled and its timer stops.</summary>
    public void OnNavigatedFrom()
    {
        _shown = false;
        _run?.Cancel();
        Reschedule();
    }

    /// <inheritdoc cref="RefreshCommand"/>
    public Task RefreshAsync() => ReadAsync(PageRead.Refresh);

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

    /// <summary>The page couldn't read what it judges: the verdict says so.</summary>
    /// <param name="what">"the sessions", "the backup history".</param>
    public void ShowError(string what, Exception error) =>
        Verdict = PageVerdict.Unreadable(what, ErrorStatus.Format(error));

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
            await LoadAsync(run.Token);
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

    // The timer runs while the page is shown, has an interval and isn't paused.
    private void Reschedule()
    {
        _schedule?.Dispose();
        _schedule = _shown && !IsPaused && _refreshInterval is { } interval && _scheduler is not null
            ? _scheduler.Every(interval, () => ReadAsync(PageRead.Timed))
            : null;
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
