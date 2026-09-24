using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace SqlVitals.Engine.History;

/// <summary>
/// Saves history records on a background thread so live monitoring never waits on the disk and
/// never sees a history failure. Records wait in a bounded queue; if the writer falls behind the
/// oldest are dropped. Every error is caught and reported (at most once a minute), and a store
/// that can't be opened is retried on a later batch.
///
/// The store is only ever touched on that thread: <see cref="ClearAsync"/> and a change of
/// <see cref="Retention"/> go through the same queue as the records.
/// </summary>
public sealed class HistoryWriter : IHistorySink, IDisposable
{
    public const int QueueCapacity = 5_000;

    private const int MaxBatch = 500;

    private static readonly TimeSpan PurgeEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan ReopenWait = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(3);

    // Holds HistoryRecords and Commands, in the order they were queued.
    private readonly Channel<object>     _queue;
    private readonly RateLimitedReporter _reporter;
    private readonly TimeProvider        _time;
    private readonly Task                _loop;

    private HistoryStore?  _store;
    private DateTimeOffset _nextOpenAttempt = DateTimeOffset.MinValue;
    private DateTimeOffset _nextPurge       = DateTimeOffset.MinValue;
    private long           _retentionTicks;
    private int            _dropped;
    private int            _clearCount;
    private volatile bool  _purgeRequested;
    private volatile bool  _disabled;

    public HistoryWriter(string path, Action<string, Exception> onError,
                         TimeSpan? retention = null, TimeProvider? time = null)
    {
        Path            = path;
        _time           = time ?? TimeProvider.System;
        _retentionTicks = CheckRetention(retention ?? TimeSpan.FromDays(HistorySettings.DefaultRetentionDays)).Ticks;
        _reporter       = new RateLimitedReporter(onError, _time);
        _queue          = Channel.CreateBounded<object>(
            new BoundedChannelOptions(QueueCapacity)
            {
                FullMode     = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            },
            OnDropped);

        _loop = Task.Run(RunAsync);
    }

    /// <summary>%LocalAppData%\SqlVitals\history.db — local, not roaming (see issue #28).</summary>
    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SqlVitals", "history.db");

    public string Path { get; }

    /// <summary>True once the file turned out to be from a newer SqlVitals; records are then discarded.</summary>
    public bool IsDisabled => _disabled;

    /// <summary>
    /// How long history is kept. Anything older is deleted every hour, and straight away when
    /// this is shortened.
    /// </summary>
    public TimeSpan Retention
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _retentionTicks));
        set
        {
            var previous = Interlocked.Exchange(ref _retentionTicks, CheckRetention(value).Ticks);
            if (value.Ticks >= previous)
                return;

            // The flag survives the command being dropped from a full queue; the command wakes
            // the writer when nothing else is being recorded.
            _purgeRequested = true;
            _queue.Writer.TryWrite(PurgeCommand.Instance);
        }
    }

    /// <summary>How many times history has been cleared; lets recorders forget what they saved.</summary>
    public int ClearCount => Volatile.Read(ref _clearCount);

    public void Enqueue(HistoryRecord record)
    {
        if (_disabled) return;
        // Never blocks: a full queue drops its oldest record instead. False only after Dispose.
        _queue.Writer.TryWrite(record);
    }

    /// <summary>
    /// Deletes all history, including records queued before the call. Completes once the file
    /// has been emptied; fails with the reason when it couldn't be.
    /// </summary>
    public Task ClearAsync()
    {
        var command = new ClearCommand();
        if (!_queue.Writer.TryWrite(command))
            return Task.FromException(new InvalidOperationException("Monitoring history has stopped."));
        return command.Done.Task;
    }

    /// <summary>
    /// Ends the alerts a previous run left active (see <see cref="HistoryStore.EndAlertsLeftOpen"/>).
    /// Call it before anything is recorded, so it runs ahead of this run's alerts. Never throws.
    /// </summary>
    public void EndAlertsLeftOpen() => _queue.Writer.TryWrite(EndAlertsCommand.Instance);

    private void OnDropped(object item)
    {
        switch (item)
        {
            case HistoryRecord:
                Interlocked.Increment(ref _dropped);
                break;
            case ClearCommand clear:
                clear.Done.TrySetException(new InvalidOperationException(
                    "Monitoring history is too busy to clear right now. Try again in a minute."));
                break;
        }
    }

    private async Task RunAsync()
    {
        var batch = new List<HistoryRecord>(MaxBatch);
        try
        {
            while (await _queue.Reader.WaitToReadAsync().ConfigureAwait(false))
            {
                ReportDropped();

                for (var read = 0; read < MaxBatch && _queue.Reader.TryRead(out var item); read++)
                {
                    if (item is HistoryRecord record)
                    {
                        batch.Add(record);
                        continue;
                    }

                    // Records queued before a command are saved first, so a clear removes them too.
                    Flush(batch);
                    Run((Command)item);
                }

                Flush(batch);
                PurgeIfDue();
            }
        }
        catch (Exception ex)
        {
            // Nothing above should throw, but if it does history stops, not the app.
            _reporter.Report("Monitoring history stopped unexpectedly.", ex);
            _queue.Writer.TryComplete();
            while (_queue.Reader.TryRead(out var item))
                if (item is ClearCommand clear)
                    clear.Done.TrySetException(new InvalidOperationException("Monitoring history has stopped.", ex));
        }
        finally
        {
            _store?.Dispose();
            _store = null;
        }
    }

    private void ReportDropped()
    {
        var dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped > 0)
            _reporter.Report($"Monitoring history fell behind; {dropped} record(s) were not saved.",
                             new InvalidOperationException("History queue full."));
    }

    private void Flush(List<HistoryRecord> batch)
    {
        if (batch.Count == 0) return;
        WriteBatch(batch);
        batch.Clear();
    }

    private void WriteBatch(List<HistoryRecord> batch)
    {
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

    private void Run(Command command)
    {
        switch (command)
        {
            case PurgeCommand:
                // Only wakes the loop, which then purges. Don't create a file just to purge it.
                if (!_disabled && File.Exists(Path))
                    TryOpen();
                break;
            case ClearCommand clear:
                Clear(clear);
                break;
            case EndAlertsCommand:
                EndAlerts();
                break;
        }
    }

    private void EndAlerts()
    {
        // Don't create a file just to find nothing in it.
        if (_disabled || !File.Exists(Path) || !TryOpen())
            return;

        try
        {
            _store!.EndAlertsLeftOpen();
        }
        catch (Exception ex)
        {
            _reporter.Report("Could not end the alerts left active by the previous run.", ex);
        }
    }

    private void Clear(ClearCommand command)
    {
        try
        {
            if (_disabled)
                throw new InvalidOperationException(
                    $"{Path} was written by a newer SqlVitals, so this version leaves it alone.");

            if (_store is null)
            {
                if (!File.Exists(Path))
                {
                    command.Done.TrySetResult();   // nothing recorded yet
                    return;
                }
                OpenStore();
            }

            _store!.Clear();
            Interlocked.Increment(ref _clearCount);
            command.Done.TrySetResult();
        }
        catch (Exception ex)
        {
            if (ex is HistorySchemaTooNewException)
                _disabled = true;
            else if (ex is not InvalidOperationException)
                Reset(quarantine: false);   // reopened on the next batch

            _reporter.Report($"Could not clear the monitoring history in {Path}.", ex);
            command.Done.TrySetException(ex);
        }
    }

    private bool TryOpen()
    {
        if (_store is not null) return true;
        if (_time.GetUtcNow() < _nextOpenAttempt) return false;

        try
        {
            OpenStore();
            return true;
        }
        catch (HistorySchemaTooNewException ex)
        {
            _disabled = true;
            _reporter.Report(ex.Message, ex);
        }
        catch (Exception ex)
        {
            _nextOpenAttempt = _time.GetUtcNow() + ReopenWait;
            _reporter.Report($"Could not open the monitoring history file {Path}.", ex);
        }
        return false;
    }

    private void OpenStore()
    {
        var store = new HistoryStore(Path);
        try
        {
            store.Open();
        }
        catch
        {
            store.Dispose();
            throw;
        }
        _store = store;
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
        if (now < _nextPurge && !_purgeRequested) return;
        _purgeRequested = false;
        _nextPurge      = now + PurgeEvery;

        try
        {
            _store.Purge((now - Retention).UtcDateTime);
        }
        catch (Exception ex)
        {
            _reporter.Report("Could not remove old monitoring history.", ex);
        }
    }

    private static TimeSpan CheckRetention(TimeSpan retention) =>
        retention > TimeSpan.Zero
            ? retention
            : throw new ArgumentOutOfRangeException(nameof(retention), retention, "Retention must be positive.");

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

    private abstract class Command;

    private sealed class PurgeCommand : Command
    {
        public static readonly PurgeCommand Instance = new();
    }

    private sealed class EndAlertsCommand : Command
    {
        public static readonly EndAlertsCommand Instance = new();
    }

    private sealed class ClearCommand : Command
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
