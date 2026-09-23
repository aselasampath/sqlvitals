using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace SqlVitals.Engine.History;

/// <summary>
/// Saves history records on a background thread so live monitoring never waits on the disk and
/// never sees a history failure. Records wait in a bounded queue; if the writer falls behind the
/// oldest are dropped. Every error is caught and reported (at most once a minute), and a store
/// that can't be opened is retried on a later batch.
/// </summary>
public sealed class HistoryWriter : IHistorySink, IDisposable
{
    public const int DefaultRetentionDays = 14;
    public const int QueueCapacity        = 5_000;

    private const int MaxBatch = 500;

    private static readonly TimeSpan PurgeEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan ReopenWait = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(3);

    private readonly Channel<HistoryRecord> _queue;
    private readonly RateLimitedReporter    _reporter;
    private readonly TimeProvider           _time;
    private readonly TimeSpan               _retention;
    private readonly Task                   _loop;

    private HistoryStore?  _store;
    private DateTimeOffset _nextOpenAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextPurge       = DateTimeOffset.MinValue;
    private int            _dropped;
    private volatile bool  _disabled;

    public HistoryWriter(string path, Action<string, Exception> onError,
                         TimeSpan? retention = null, TimeProvider? time = null)
    {
        Path       = path;
        _time      = time ?? TimeProvider.System;
        _retention = retention ?? TimeSpan.FromDays(DefaultRetentionDays);
        _reporter  = new RateLimitedReporter(onError, _time);
        _queue     = Channel.CreateBounded<HistoryRecord>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode     = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            _ => Interlocked.Increment(ref _dropped));

        _loop = Task.Run(RunAsync);
    }

    /// <summary>%LocalAppData%\SqlVitals\history.db — local, not roaming (see issue #28).</summary>
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SqlVitals", "history.db");

    public string Path { get; }

    /// <summary>True once the file turned out to be from a newer SqlVitals; records are then discarded.</summary>
    public bool IsDisabled => _disabled;

    public void Enqueue(HistoryRecord record)
    {
        if (_disabled) return;
        // Never blocks: a full queue drops its oldest record instead. False only after Dispose.
        _queue.Writer.TryWrite(record);
    }

    private async Task RunAsync()
    {
        var batch = new List<HistoryRecord>(MaxBatch);
        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (batch.Count < MaxBatch && _queue.Reader.TryRead(out var record))
                    batch.Add(record);

                WriteBatch(batch);
                batch.Clear();
                PurgeIfDue();
            }
        }
        catch (Exception ex)
        {
            // Nothing above should throw, but if it does history stops, not the app.
            _reporter.Report("Monitoring history stopped unexpectedly.", ex);
        }
        finally
        {
            _store?.Dispose();
            _store = null;
        }
    }

    private void WriteBatch(List<HistoryRecord> batch)
    {
        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
            _reporter.Report($"Monitoring history fell behind; {dropped} record(s) were not saved.",
                             new InvalidOperationException("History queue full."));

        if (_disabled || !TryOpen())
            return;

        try
        {
            _store!.Write(batch);
        }
        catch (SqliteException ex) when (HistoryStore.IsCorruption(ex))
        {
            _reporter.Report($"The monitoring history file {Path} is damaged; starting a new one.", ex);
            Reset(quarantine: true);
        }
        catch (Exception ex)
        {
            // Disk full, file locked by antivirus… this batch is lost; the next one tries again.
            _reporter.Report($"Could not save monitoring history to {Path}.", ex);
            Reset(quarantine: false);
        }
    }

    private bool TryOpen()
    {
        if (_store is not null) return true;
        if (_time.GetUtcNow() < _nextOpenAttempt) return false;

        var store = new HistoryStore(Path);
        try
        {
            store.Open();
            _store = store;
            return true;
        }
        catch (HistorySchemaTooNewException ex)
        {
            store.Dispose();
            _disabled = true;
            _reporter.Report(ex.Message, ex);
        }
        catch (Exception ex)
        {
            store.Dispose();
            _nextOpenAttempt = _time.GetUtcNow() + ReopenWait;
            _reporter.Report($"Could not open the monitoring history file {Path}.", ex);
        }
        return false;
    }

    private void Reset(bool quarantine)
    {
        _store?.Dispose();
        _store = null;
        if (!quarantine) return;

        try
        {
            HistoryStore.Quarantine(Path);
        }
        catch (Exception ex)
        {
            _reporter.Report($"Could not move the damaged history file {Path} aside.", ex);
            _nextOpenAttempt = _time.GetUtcNow() + ReopenWait;
        }
    }

    private void PurgeIfDue()
    {
        if (_store is null) return;

        var now = _time.GetUtcNow();
        if (now < _nextPurge) return;
        _nextPurge = now + PurgeEvery;

        try
        {
            _store.Purge((now - _retention).UtcDateTime);
        }
        catch (Exception ex)
        {
            _reporter.Report("Could not remove old monitoring history.", ex);
        }
    }

    /// <summary>Stops taking records and gives queued ones a few seconds to be saved.</summary>
    public void Dispose()
    {
        if (!_queue.Writer.TryComplete())
            return;

        try
        {
            _loop.Wait(DisposeWait);
        }
        catch (Exception)
        {
            // RunAsync reports its own failures.
        }
    }
}
