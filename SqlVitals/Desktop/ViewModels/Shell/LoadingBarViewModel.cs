using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Loading;

namespace SqlVitals.Desktop.ViewModels.Shell;

/// <summary>
/// The loading bar across the top of the page area (#117): what SqlVitals is waiting on, for how
/// long, when it gives up, and Stop. The shell begins a wait for each read the user waits on;
/// <see cref="LoadingIndicator"/> decides when the bar shows and what it says, and this asks it
/// again whenever that may change, on the app's clock. <c>Controls/LoadingBar</c> draws it.
/// </summary>
public sealed partial class LoadingBarViewModel(TimeProvider time, IRefreshScheduler scheduler) : ObservableObject
{
    private readonly LoadingIndicator _indicator = new();

    // What the bar does for each wait running, by its number: its Stop, whether the page dims,
    // whether it is the page's own read, and whether Stop was pressed on it.
    private sealed class Running(Action? stop, bool dims, bool ofPage)
    {
        public Action? Stop { get; } = stop;
        public bool Dims { get; } = dims;
        public bool OfPage { get; } = ofPage;
        public bool Stopping { get; set; }
    }

    private readonly Dictionary<long, Running> _running = [];

    // The wait on the bar, and the next time it may say something else.
    private long? _shownId;
    private IRefreshSchedule? _wake;

    /// <summary>The bar is on screen.</summary>
    [ObservableProperty]
    private bool _isShown;

    /// <summary>"Loading Backups… 7 s"; empty while hidden.</summary>
    [ObservableProperty]
    private string _text = string.Empty;

    /// <summary>Stop is offered: the wait shown is running and can be stopped.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _canStop;

    /// <summary>The page is refreshing what it shows: dimmed until the new rows arrive.</summary>
    [ObservableProperty]
    private bool _dimsPage;

    /// <summary>What a screen reader hears: "Loading Backups" as the bar shows, "Backups loaded" as it goes.</summary>
    public string Announcement { get; private set; } = string.Empty;

    /// <summary>A wait ended after Stop was pressed on it.</summary>
    public event Action<StoppedWait>? Stopped;

    /// <summary>
    /// A wait began. The bar shows it once it has run <see cref="LoadingIndicator.ShowAfter"/>,
    /// until a later one begins or it ends.
    /// </summary>
    /// <param name="stop">What Stop does; null when the wait can't be stopped.</param>
    /// <param name="dims">The page dims while it is shown: a Refresh of what is on screen.</param>
    /// <param name="ofPage">It is the page's own read, not a part of it read on its own.</param>
    public IReading Begin(Wait wait, Action? stop, bool dims, bool ofPage = false)
    {
        var id = _indicator.Begin(wait with { CanStop = stop is not null }, time.GetUtcNow());
        _running[id] = new Running(stop, dims, ofPage);
        Update();
        return new Reading(this, id);
    }

    /// <summary>The page or the server changed: every wait belongs to what was left, and the bar goes at once.</summary>
    public void Clear()
    {
        _indicator.Clear();
        _running.Clear();
        Update();
    }

    [RelayCommand(CanExecute = nameof(CanStop))]
    private void Stop()
    {
        if (_shownId is not { } id || !_running.TryGetValue(id, out var running) || running.Stop is null)
            return;
        running.Stopping = true;
        CanStop = false;
        try
        {
            running.Stop();
        }
        catch (Exception ex)
        {
            AppLog.Error("Loading bar: could not stop the read", ex);
        }
    }

    // A wait ended: Done when it read. One the bar no longer knows (cleared) changes nothing.
    private void End(long id, bool done)
    {
        if (_indicator.Running(id) is not { } ended || !_running.Remove(id, out var running))
            return;
        _indicator.End(id);
        var waited = time.GetUtcNow() - ended.Began;

        if (running.Stopping && !done)
            Stopped?.Invoke(new StoppedWait(ended.Wait, waited, running.OfPage));
        else if (done && _shownId == id && ended.Wait.Done is { } said)
            Announce(said);
        Update();
    }

    // Asks the rules what the bar shows now, and when to ask again.
    private void Update()
    {
        _wake?.Dispose();
        _wake = null;

        var now  = time.GetUtcNow();
        var view = _indicator.Show(now);
        // Heard as it appears, and as another wait takes its place; not as it lingers.
        var announces = view is { Lingering: false } && view.Id != _shownId;

        _shownId = view?.Id;
        Text     = view?.Text ?? string.Empty;
        CanStop  = view is { CanStop: true } && _running.TryGetValue(view.Id, out var stoppable) && !stoppable.Stopping;
        DimsPage = view is { Lingering: false } && _running.TryGetValue(view.Id, out var dimming) && dimming.Dims;
        IsShown  = view is not null;
        if (announces && _indicator.Running(view!.Id) is { } shown)
            Announce(shown.Wait.Doing);

        if (_indicator.NextChange(now) is { } next)
            _wake = scheduler.Every(next - now, WakeAsync);
    }

    private Task WakeAsync()
    {
        Update();
        return Task.CompletedTask;
    }

    private void Announce(string text)
    {
        Announcement = text;
        // Raised every time: a screen reader hears the same words again.
        OnPropertyChanged(nameof(Announcement));
    }

    /// <summary>A wait ended after Stop was pressed on it.</summary>
    /// <param name="Waited">How long it had run.</param>
    /// <param name="OfPage">It was the page's own read, not a part of it read on its own.</param>
    public sealed record StoppedWait(Wait Wait, TimeSpan Waited, bool OfPage);

    private sealed class Reading(LoadingBarViewModel bar, long id) : IReading
    {
        private bool _ended;

        public void Done() => End(done: true);

        public void Dispose() => End(done: false);

        private void End(bool done)
        {
            if (_ended)
                return;
            _ended = true;
            bar.End(id, done);
        }
    }
}
