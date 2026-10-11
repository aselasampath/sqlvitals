using System.Globalization;
using SqlVitals.Engine.Backups;

namespace SqlVitals.Engine.Monitoring;

/// <summary>A volume that holds database files, as sys.dm_os_volume_stats reports it (#174).</summary>
/// <param name="Volume">Its mount point: "E:\", "/var/opt/mssql/".</param>
public sealed record VolumeSpace(string Volume, double TotalGb, double FreeGb)
{
    /// <summary>Free space as a share of the volume; 0 when its size isn't known.</summary>
    public double FreePct => TotalGb > 0 ? FreeGb / TotalGb * 100 : 0;
}

/// <summary>What a read of the slow checks asks the server (#174).</summary>
/// <param name="Rpo">The RPO set on the Backups page, that databases are checked against.</param>
/// <param name="JobsAfterInstanceId">The newest msdb job history row the last read saw: failures after it count. Null on the first read.</param>
/// <param name="FirstLookBack">On the first read, failures that ended this long ago or less count.</param>
/// <param name="JobsServer">The @@SERVERNAME whose job history <paramref name="JobsAfterInstanceId"/> is a row of: after a
/// failover to another replica it means nothing, and the read starts again as a first one.</param>
public sealed record ServerChecksRequest(BackupRpo Rpo, long? JobsAfterInstanceId, TimeSpan FirstLookBack, string? JobsServer = null);

/// <summary>
/// What the server said for the slow checks at one read (#174). Each part is null, with why in
/// its NoData text, when it couldn't be read or doesn't apply (Azure SQL Database has no msdb
/// jobs, backups or volumes to read).
/// </summary>
/// <param name="PastRpo">The databases past their backup RPO, most urgent first.</param>
/// <param name="FailedJobs">The Agent jobs that failed since <see cref="ServerChecksRequest.JobsAfterInstanceId"/>.</param>
/// <param name="LastJobInstanceId">The newest msdb job history row now, for the next read; null when the jobs couldn't be read.</param>
/// <param name="JobsServer">The @@SERVERNAME the job history was read on.</param>
public sealed record ServerCheckFigures(
    IReadOnlyList<VolumeSpace>? Volumes,
    string?                     VolumesNoData,
    IReadOnlyList<string>?      PastRpo,
    string?                     BackupsNoData,
    IReadOnlyList<string>?      FailedJobs,
    string?                     JobsNoData,
    long?                       LastJobInstanceId = null,
    string?                     JobsServer        = null);

/// <summary>
/// The checks that change slowly (#174): free disk space, backups past their RPO and failed Agent
/// jobs. Unlike the other six, which are graded on every sample, these are read every
/// <see cref="Interval"/> (the history's detail interval, 5 minutes by default), and the last read
/// is graded with each sample. A read older than <see cref="StaleAfter"/> (two intervals and a
/// minute: one missed read is fine, two aren't) is too old to trust, and its checks are
/// <see cref="HealthLevel.Unknown"/> until the next read, as is a check the server couldn't answer.
/// A failed job is reported for <see cref="FailureHold"/>: a check interval, or longer when an
/// alert needs more samples than that to start.
/// <para>Not thread-safe: the <see cref="Collector"/> uses it under its lock.</para>
/// </summary>
public sealed class ServerChecks
{
    /// <summary>The checks read here, in <see cref="HealthIndicator"/> order.</summary>
    public static IReadOnlyList<HealthIndicator> Indicators { get; } =
        [HealthIndicator.DiskFree, HealthIndicator.BackupsPastRpo, HealthIndicator.FailedJobs];

    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(History.HistorySettings.DefaultIntervalMinutes);

    /// <summary>How late a read may be on top of two intervals before it's too old to trust.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    private TimeSpan            _interval       = DefaultInterval;
    private TimeSpan            _sampleInterval = TimeSpan.FromSeconds(Collector.DefaultIntervalSeconds);
    private int                 _alertSamples   = Alerts.AlertSettings.DefaultSamples;
    private ServerCheckFigures? _figures;
    private DateTime?           _readUtc;
    private DateTime?           _attemptUtc;
    private long?               _lastJobInstanceId;
    private string?             _jobsServer;

    // Each job seen failing, and when it was last seen in a read.
    private readonly Dictionary<string, DateTime> _failed = new(StringComparer.Ordinal);

    /// <summary>Time between reads, counted from the start of the last one. A change applies to the next check.</summary>
    public TimeSpan Interval
    {
        get => _interval;
        set => _interval = value > TimeSpan.Zero
            ? value
            : throw new ArgumentOutOfRangeException(nameof(value), value, "The interval must be positive.");
    }

    /// <summary>
    /// Time between samples. The checks are read after a sample, so with samples further apart
    /// than <see cref="Interval"/> they're read that often instead.
    /// </summary>
    public TimeSpan SampleInterval
    {
        get => _sampleInterval;
        set => _sampleInterval = value > TimeSpan.Zero ? value : _sampleInterval;
    }

    /// <summary>Samples in a row an alert needs to start; a failed job is held long enough for one.</summary>
    public int AlertSamples
    {
        get => _alertSamples;
        set => _alertSamples = Math.Max(1, value);
    }

    /// <summary>The time between reads: <see cref="Interval"/>, or the sample interval when that is longer.</summary>
    public TimeSpan Every => Interval > SampleInterval ? Interval : SampleInterval;

    /// <summary>A read older than this is too old to trust: two reads' time and <see cref="Grace"/>.</summary>
    public TimeSpan StaleAfter => Every * 2 + Grace;

    /// <summary>
    /// How long a failed job counts after it was last seen: a read's time, or as long as an alert
    /// needs to start when that is longer, so a single failure can always raise one.
    /// </summary>
    public TimeSpan FailureHold
    {
        get
        {
            var alert = SampleInterval * (AlertSamples + 1);
            return alert > Every ? alert : Every;
        }
    }

    /// <summary>When the last read that worked finished, UTC; null before one has.</summary>
    public DateTime? ReadUtc => _readUtc;

    public static bool IsServerCheck(HealthIndicator indicator) => Indicators.Contains(indicator);

    /// <summary>No read yet, or an interval has passed since the last one started.</summary>
    public bool IsDue(DateTime nowUtc) => _attemptUtc is not { } last || nowUtc >= last + Interval;

    /// <summary>
    /// A read starts: the next is due an interval from now, whether this one works or not. What
    /// to ask, with the job history row the last read saw.
    /// </summary>
    public ServerChecksRequest Start(DateTime nowUtc, BackupRpo rpo)
    {
        _attemptUtc = nowUtc;
        return new ServerChecksRequest(rpo, _lastJobInstanceId, Every, _jobsServer);
    }

    /// <summary>A read worked: its figures are graded from now on.</summary>
    public void Apply(ServerCheckFigures figures, DateTime nowUtc)
    {
        _figures = figures;
        _readUtc = nowUtc;
        // A read that couldn't see the job history keeps the last row seen, so the next one that
        // can still counts every failure since.
        if (figures.LastJobInstanceId is { } id)
        {
            _lastJobInstanceId = id;
            _jobsServer        = figures.JobsServer;
        }
        foreach (var job in figures.FailedJobs ?? [])
            _failed[job] = nowUtc;
        foreach (var job in _failed.Where(f => nowUtc - f.Value >= FailureHold).Select(f => f.Key).ToList())
            _failed.Remove(job);
    }

    /// <summary>Forgets the last read, e.g. when the connection changed; the next is due straight away.</summary>
    public void Reset()
    {
        _figures = null;
        _readUtc = _attemptUtc = null;
        _lastJobInstanceId = null;
        _jobsServer        = null;
        _failed.Clear();
    }

    /// <summary>
    /// The three checks graded on <paramref name="thresholds"/>, in <see cref="Indicators"/> order:
    /// the last read's figures while it can be trusted, otherwise <see cref="HealthLevel.Unknown"/>
    /// with why in the reading's note.
    /// </summary>
    public IReadOnlyList<HealthReading> Readings(HealthThresholds thresholds, DateTime nowUtc)
    {
        if (_figures is not { } figures || _readUtc is not { } read)
            return Unknown(thresholds, $"Not read yet: read every {Text(Every)}.");

        var age = nowUtc - read;
        if (age > StaleAfter)
            return Unknown(thresholds, $"Last read {Text(age)} ago, too old to trust: it is read every {Text(Every)}.");

        // The jobs that failed since the last read, and those seen failing within the hold.
        var failed = figures.FailedJobs is null
            ? null
            : _failed.Where(f => nowUtc - f.Value < FailureHold).OrderByDescending(f => f.Value).Select(f => f.Key).ToList();

        return
        [
            figures.Volumes is { } volumes
                ? volumes.Count > 0
                    ? HealthRules.Read(HealthIndicator.DiskFree, 0, measured: true, new HealthFacts(Volumes: volumes), thresholds)
                    : NoData(HealthIndicator.DiskFree, thresholds, "No volume holding database files was reported.")
                : NoData(HealthIndicator.DiskFree, thresholds, figures.VolumesNoData),
            Count(HealthIndicator.BackupsPastRpo, figures.PastRpo, figures.BackupsNoData, thresholds),
            Count(HealthIndicator.FailedJobs, failed, figures.JobsNoData, thresholds),
        ];
    }

    private static HealthReading Count(HealthIndicator indicator, IReadOnlyList<string>? names, string? noData, HealthThresholds thresholds) =>
        names is null
            ? NoData(indicator, thresholds, noData)
            : HealthRules.Read(indicator, names.Count, measured: true, new HealthFacts(Names: names), thresholds);

    private static IReadOnlyList<HealthReading> Unknown(HealthThresholds thresholds, string why) =>
        Indicators.Select(i => NoData(i, thresholds, why)).ToList();

    private static HealthReading NoData(HealthIndicator indicator, HealthThresholds thresholds, string? why) =>
        HealthRules.Read(indicator, 0, measured: false, HealthFacts.None, thresholds) with
        {
            Note = string.IsNullOrWhiteSpace(why) ? "The server didn't report it." : why,
        };

    // "5 min", "1 h 10 min", "45 s".
    private static string Text(TimeSpan span) => span switch
    {
        _ when span < TimeSpan.FromMinutes(1) => string.Format(CultureInfo.CurrentCulture, "{0:0} s", Math.Max(0, span.TotalSeconds)),
        _ when span < TimeSpan.FromHours(1)   => string.Format(CultureInfo.CurrentCulture, "{0:0} min", Math.Floor(span.TotalMinutes)),
        _ => span.Minutes == 0
            ? string.Format(CultureInfo.CurrentCulture, "{0} h", (int)span.TotalHours)
            : string.Format(CultureInfo.CurrentCulture, "{0} h {1} min", (int)span.TotalHours, span.Minutes),
    };
}
