using System.Globalization;

namespace SqlVitals.Engine.Loading;

/// <summary>How the loading text words a wait's time limit (#117).</summary>
public enum WaitLimitKind
{
    /// <summary>Each of the read's queries has this limit: "each query gives up after 30 s".</summary>
    EachQuery,

    /// <summary>Some queries have a longer limit of their own: "each query gives up after at most 2 min".</summary>
    EachQueryAtMost,

    /// <summary>The whole wait has this limit: "gives up after 15 s".</summary>
    Whole,
}

/// <summary>When a wait gives up, and the setting that changes it.</summary>
/// <param name="Setting">Where it is set, said after the limit: "Settings → Command timeout"; null for none.</param>
public sealed record WaitLimit(TimeSpan After, WaitLimitKind Kind, string? Setting = null);

/// <summary>
/// Something the user waits on (#117): "Loading Backups", and what is said when it's done.
/// </summary>
/// <param name="Doing">What is happening, without the dots: "Loading Backups", "Checking fragmentation".</param>
/// <param name="Done">Said to a screen reader when it finishes: "Backups loaded"; null says nothing.</param>
/// <param name="Limit">When it gives up; null when that isn't known, and only the time waited is said.</param>
/// <param name="Note">Said after the rest: "complete the Microsoft sign-in if prompted"; null for none.</param>
/// <param name="CanStop">The bar offers Stop for it.</param>
public sealed record Wait(string Doing, string? Done = null, WaitLimit? Limit = null, string? Note = null, bool CanStop = false);

/// <summary>What the loading bar shows now (#117).</summary>
/// <param name="Id">The wait shown, as <see cref="LoadingIndicator.Begin"/> numbered it.</param>
/// <param name="Text">"Loading Index Health… 12 s · each query gives up after 30 s (Settings → Command timeout)".</param>
/// <param name="CanStop">Stop is offered: the wait shown is still running and can be stopped.</param>
/// <param name="Lingering">The waits have ended, and the bar stays for its shortest time.</param>
public sealed record LoadingView(long Id, string Text, bool CanStop, bool Lingering);

/// <summary>
/// When the loading bar shows and what it says (#117), as pure rules over the time it is asked
/// at: a wait shows once it has run <see cref="ShowAfter"/>, so fast reads don't flicker; once
/// shown, the bar stays at least <see cref="ShownAtLeast"/>; the text adds the time waited from
/// <see cref="CountsFrom"/> and when it gives up from <see cref="LimitFrom"/>. Of the waits
/// running, the one begun last is shown. The app asks it again at <see cref="NextChange"/>.
/// </summary>
/// <remarks>
/// It never hears of a wait that belongs to a page no longer on screen: the app calls
/// <see cref="Clear"/> as it moves, and an end it hears of afterwards is ignored.
/// </remarks>
public sealed class LoadingIndicator
{
    public static readonly TimeSpan ShowAfter    = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan ShownAtLeast = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan CountsFrom   = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan LimitFrom    = TimeSpan.FromSeconds(10);

    private sealed record Entry(long Id, Wait Wait, DateTimeOffset Began);

    // Running, in the order they began.
    private readonly List<Entry> _running = [];
    private long _nextId;

    // The bar is on screen since then, last showing that wait; null while hidden.
    private DateTimeOffset? _shownSince;
    private Entry? _lastShown;

    /// <summary>A wait began: it shows if it is still running <see cref="ShowAfter"/> from now.</summary>
    /// <returns>Its number, for <see cref="End"/>.</returns>
    public long Begin(Wait wait, DateTimeOffset now)
    {
        var entry = new Entry(++_nextId, wait, now);
        _running.Add(entry);
        return entry.Id;
    }

    /// <summary>A wait ended, however it went. False for one already ended or cleared.</summary>
    public bool End(long id) => _running.RemoveAll(e => e.Id == id) > 0;

    /// <summary>A wait still running, and when it began; null once it ended.</summary>
    public (Wait Wait, DateTimeOffset Began)? Running(long id) =>
        _running.FirstOrDefault(e => e.Id == id) is { } entry ? (entry.Wait, entry.Began) : null;

    /// <summary>
    /// Every wait belongs to what the user has left (another page, another server): the bar goes at
    /// once, without its shortest time, so the next page's wait starts afresh.
    /// </summary>
    public void Clear()
    {
        _running.Clear();
        _shownSince = null;
        _lastShown  = null;
    }

    /// <summary>What the bar shows at <paramref name="now"/>; null for nothing. Asked in time order.</summary>
    public LoadingView? Show(DateTimeOffset now)
    {
        // The latest wait that has run long enough to show.
        var due = _running.LastOrDefault(e => now - e.Began >= ShowAfter);
        if (due is not null)
        {
            _shownSince ??= now;
            _lastShown    = due;
            return new LoadingView(due.Id, Text(due.Wait, now - due.Began), due.Wait.CanStop, Lingering: false);
        }

        if (_shownSince is { } since && _lastShown is { } last && now - since < ShownAtLeast)
        {
            // Ended too soon after it showed: the bar stays as it was, without Stop.
            return new LoadingView(last.Id, Text(last.Wait, now - last.Began), CanStop: false, Lingering: true);
        }

        _shownSince = null;
        _lastShown  = null;
        return null;
    }

    /// <summary>
    /// When <see cref="Show"/> may say something else, after <paramref name="now"/>: a wait due to
    /// show, the shortest time ending, or the next second of the time waited. Null while nothing would.
    /// </summary>
    public DateTimeOffset? NextChange(DateTimeOffset now)
    {
        DateTimeOffset? next = null;
        void Consider(DateTimeOffset at)
        {
            if (at > now && (next is null || at < next))
                next = at;
        }

        foreach (var entry in _running)
        {
            var shows = entry.Began + ShowAfter;
            if (shows > now)
                Consider(shows);
            else
            {
                // Shown, or shown as soon as the one in front ends: its text counts the seconds.
                var waited = now - entry.Began;
                Consider(waited < CountsFrom
                    ? entry.Began + CountsFrom
                    : entry.Began + TimeSpan.FromSeconds(Math.Floor(waited.TotalSeconds) + 1));
            }
        }

        // Lingering after the waits shown ended: until its shortest time is up.
        if (_shownSince is { } since && !_running.Any(e => now - e.Began >= ShowAfter))
            Consider(since + ShownAtLeast);
        return next;
    }

    /// <summary>
    /// "Loading Backups…", then "Loading Backups… 7 s" from 3 s, then with when it gives up from
    /// 10 s: "Loading Backups… 12 s · each query gives up after 30 s (Settings → Command timeout)".
    /// </summary>
    public static string Text(Wait wait, TimeSpan waited)
    {
        var text   = wait.Doing + "…";
        var counts = waited >= CountsFrom;
        if (counts)
            text += $" {(int)waited.TotalSeconds} s";

        var extras = new List<string>(2);
        if (waited >= LimitFrom && wait.Limit is { } limit)
            extras.Add(LimitText(limit));
        if (!string.IsNullOrWhiteSpace(wait.Note))
            extras.Add(wait.Note.Trim());

        return extras.Count == 0 ? text : text + (counts ? " · " : " ") + string.Join(" · ", extras);
    }

    /// <summary>"each query gives up after 30 s (Settings → Command timeout)", "gives up after 2 min".</summary>
    public static string LimitText(WaitLimit limit)
    {
        var after = limit.Kind switch
        {
            WaitLimitKind.EachQuery       => "each query gives up after ",
            WaitLimitKind.EachQueryAtMost => "each query gives up after at most ",
            _                             => "gives up after ",
        } + Duration(limit.After);
        return limit.Setting is null ? after : $"{after} ({limit.Setting})";
    }

    /// <summary>"30 s", "2 min", "90 s": whole minutes in minutes, anything else in seconds.</summary>
    public static string Duration(TimeSpan time)
    {
        var seconds = (long)Math.Round(time.TotalSeconds);
        return seconds >= 60 && seconds % 60 == 0
            ? string.Create(CultureInfo.CurrentCulture, $"{seconds / 60} min")
            : string.Create(CultureInfo.CurrentCulture, $"{seconds} s");
    }

    /// <summary>"14 s" waited, for "Stopped after 14 s": whole seconds, at least 1.</summary>
    public static string Waited(TimeSpan waited) => Duration(TimeSpan.FromSeconds(Math.Max(1, Math.Floor(waited.TotalSeconds))));
}
