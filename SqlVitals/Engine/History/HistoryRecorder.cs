using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.History;

/// <summary>
/// Feeds one connection's history. Every live sample is queued as it arrives; every
/// <see cref="DetailInterval"/> a detail snapshot (waits by type, file I/O, memory, top queries)
/// is read from the server and queued too. Nothing here throws to the caller: a failure is
/// reported and the next attempt waits for the next interval, so live monitoring is unaffected.
/// </summary>
public sealed class HistoryRecorder
{
    /// <summary>Queries stored per detail snapshot, highest CPU in the interval first.</summary>
    public const int TopQueries = 20;

    /// <summary>Longest statement text stored.</summary>
    public const int MaxQueryTextLength = 4_000;

    // The first read takes every cached query as the baseline; later reads only what ran since.
    internal const int BaselineQueryLimit = 20_000;
    internal const int IntervalQueryLimit = 500;

    // Query texts already queued this session, so each is fetched once rather than every snapshot.
    private const int MaxRememberedTexts = 10_000;

    private readonly IHistoryRepository   _repository;
    private readonly IHistorySink         _sink;
    private readonly RateLimitedReporter  _reporter;
    private readonly TimeProvider         _time;
    private readonly HistoryDetailTracker _tracker = new();
    private readonly HashSet<string>      _textsSaved = new();

    private long            _detailIntervalTicks = TimeSpan.FromMinutes(HistorySettings.DefaultIntervalMinutes).Ticks;
    private DateTimeOffset? _lastDetail;
    private int             _detailInFlight;
    private int             _clearCountSeen;

    public HistoryRecorder(HistoryConnection connection, IHistoryRepository repository, IHistorySink sink,
                           Action<string, Exception> onError, TimeProvider? time = null)
    {
        Connection  = connection;
        _repository = repository;
        _sink       = sink;
        _time       = time ?? TimeProvider.System;
        _reporter   = new RateLimitedReporter(onError, _time);
    }

    public HistoryConnection Connection { get; }

    /// <summary>
    /// Time between detail snapshots, counted from the end of the previous one. A change applies
    /// to the next check, so a shorter interval can make a snapshot due straight away.
    /// </summary>
    public TimeSpan DetailInterval
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _detailIntervalTicks));
        set
        {
            if (value <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(value), value, "The detail interval must be positive.");
            Interlocked.Exchange(ref _detailIntervalTicks, value.Ticks);
        }
    }

    /// <summary>Queues a live sample, stamped with this PC's UTC clock.</summary>
    public void RecordSample(LiveMetricSample sample)
    {
        try
        {
            _sink.Enqueue(new MetricSampleRecord(Connection, _time.GetUtcNow().UtcDateTime, sample));
        }
        catch (Exception ex)
        {
            _reporter.Report($"Could not queue monitoring history for {Connection.Name}.", ex);
        }
    }

    /// <summary>Queues alerts that started, changed or ended. Never throws.</summary>
    public void RecordAlerts(IReadOnlyList<AlertChange> changes)
    {
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            foreach (var change in changes)
                _sink.Enqueue(new AlertRecord(Connection, now, change.Alert));
        }
        catch (Exception ex)
        {
            _reporter.Report($"Could not queue alerts for {Connection.Name}.", ex);
        }
    }

    /// <summary>
    /// Takes a detail snapshot when one is due and none is already running. Never throws; the
    /// server reads run off the calling thread.
    /// </summary>
    public async Task CollectDetailIfDueAsync()
    {
        // _lastDetail is only read or written while holding the in-flight flag.
        if (Interlocked.Exchange(ref _detailInFlight, 1) == 1)
            return;
        if (_lastDetail is { } last && _time.GetUtcNow() < last + DetailInterval)
        {
            Volatile.Write(ref _detailInFlight, 0);
            return;
        }

        try
        {
            await CollectDetailAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _reporter.Report($"Could not collect monitoring history details for {Connection.Name}.", ex);
        }
        finally
        {
            _lastDetail = _time.GetUtcNow();
            Volatile.Write(ref _detailInFlight, 0);
        }
    }

    private async Task CollectDetailAsync()
    {
        var since = _tracker.QueriesExecutedSince;
        var snapshot = await _repository
            .GetHistoryDetailAsync(since, since is null ? BaselineQueryLimit : IntervalQueryLimit)
            .ConfigureAwait(false);
        var capturedUtc = _time.GetUtcNow().UtcDateTime;

        var detail = _tracker.Add(snapshot, TopQueries);
        if (detail is null)
            return;   // baseline: nothing to compare with yet

        // After a clear the saved texts are gone too; fetch them again as the queries show up.
        var clearCount = _sink.ClearCount;
        if (clearCount != _clearCountSeen)
        {
            _textsSaved.Clear();
            _clearCountSeen = clearCount;
        }

        IReadOnlyList<QueryTextInfo> texts = Array.Empty<QueryTextInfo>();
        var missing = detail.TopQueries.Select(q => q.QueryHash).Where(h => !_textsSaved.Contains(h)).ToList();
        if (missing.Count > 0)
        {
            try
            {
                texts = await _repository.GetQueryTextsAsync(missing, MaxQueryTextLength).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The numbers are still worth keeping; the text is fetched again next time.
                _reporter.Report($"Could not read query text for monitoring history of {Connection.Name}.", ex);
            }
        }

        _sink.Enqueue(new DetailRecord(Connection, capturedUtc, detail, texts));

        if (_textsSaved.Count + texts.Count > MaxRememberedTexts)
            _textsSaved.Clear();
        foreach (var t in texts)
            _textsSaved.Add(t.QueryHash);
    }
}
