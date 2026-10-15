using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Models;

namespace SqlVitals.Desktop.Tests.Fakes;

/// <summary>
/// An <see cref="IAlertFeed"/> with the alerts given (#187): those saved in its history, and those
/// active now. A test raises <see cref="AlertsChanged"/> and <see cref="MonitoringChanged"/> itself.
/// </summary>
internal sealed class FakeAlertFeed : IAlertFeed
{
    /// <summary>The alerts the history holds.</summary>
    public List<StoredAlert> Stored { get; } = [];

    /// <summary>What reading the history throws, instead of answering; null answers.</summary>
    public Exception? ReadFails { get; set; }

    /// <summary>The ranges asked for, in order: (connection, from, to).</summary>
    public List<(Guid? ConnectionId, DateTime FromUtc, DateTime ToUtc)> Reads { get; } = [];

    public IHistoryReader HistoryReader => new Reader(this);

    public List<Alert> Active { get; } = [];

    public IReadOnlyList<Alert> ActiveAlerts => Active;

    public int MonitoredCount { get; set; } = 1;

    /// <summary>The connections monitored now.</summary>
    public HashSet<Guid> Monitored { get; } = [];

    public bool IsMonitoring(Guid? connectionId) => connectionId is { } id && Monitored.Contains(id);

    public string NotMonitoredReason(Guid connectionId) => "Stopped by you.";

    public List<string> NotMonitored { get; } = [];

    public IReadOnlyList<string> NotMonitoredNames() => NotMonitored;

    public event Action<Guid, IReadOnlyList<AlertChange>>? AlertsChanged;
    public event Action? MonitoringChanged;

    public int Followers => (AlertsChanged?.GetInvocationList().Length ?? 0) + (MonitoringChanged?.GetInvocationList().Length ?? 0);

    public void RaiseAlertsChanged(Guid connectionId) => AlertsChanged?.Invoke(connectionId, []);

    public void RaiseMonitoringChanged() => MonitoringChanged?.Invoke();

    // The fake history's alerts in the range asked for; the rest of it is empty.
    private sealed class Reader(FakeAlertFeed feed) : IHistoryReader
    {
        private readonly FakeHistoryReader _empty = new();

        public string Path => "fake history";

        public IReadOnlyList<StoredAlert> ReadAlerts(Guid? connectionId, DateTime fromUtc, DateTime toUtc)
        {
            lock (feed.Reads)
                feed.Reads.Add((connectionId, fromUtc, toUtc));
            if (feed.ReadFails is { } error)
                throw error;
            return feed.Stored.Where(s => (connectionId is null || s.Alert.ConnectionId == connectionId)
                                          && s.Alert.StartedUtc < toUtc && (s.Alert.EndedUtc ?? DateTime.MaxValue) >= fromUtc).ToList();
        }

        public IReadOnlyList<WaitHistoryBucket> ReadWaits(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket) => [];
        public IReadOnlyList<StoredConnection> ReadConnections() => [];
        public IReadOnlyList<MetricHistoryPoint> ReadMetrics(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket) => [];
        public IReadOnlyList<CounterHistoryBucket> ReadCounters(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket) => [];
        public IReadOnlyList<HistoryQueryTotals> ReadQueryTotals(Guid connectionId, DateTime fromUtc, DateTime toUtc) => [];
        public IReadOnlyDictionary<string, QueryTextInfo> ReadQueryTexts(Guid connectionId, IReadOnlyCollection<string> queryHashes) =>
            _empty.ReadQueryTexts(connectionId, queryHashes);
        public IReadOnlyList<ScoreHistoryPoint> ReadScores(Guid connectionId, DateTime fromUtc, DateTime toUtc, TimeSpan bucket) => [];
        public ScoreHistoryPoint? ReadLastReachableScore(Guid connectionId, DateTime beforeUtc) => null;
        public DateTime? ReadLastSampleTime(Guid connectionId) => null;
        public IReadOnlyList<StoredActivity> ReadActivity(DateTime fromUtc, DateTime toUtc, Guid? connectionId = null, string? evt = null) => [];
    }
}
