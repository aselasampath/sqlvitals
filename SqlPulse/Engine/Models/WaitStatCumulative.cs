namespace SqlPulse.Engine.Models;

public record WaitStatCumulative(
    string WaitType,
    string WaitCategory,
    int IsBenign,
    long WaitingTasksCount,
    long WaitTimeMs,
    long MaxWaitTimeMs,
    long SignalWaitTimeMs,
    long ResourceWaitTimeMs,
    double AvgWaitTimeMs,
    double SignalWaitPct,
    double PctOfTotalWaits,
    double WaitTimeSec,
    DateTime CaptureTime,
    DateTime ServerStartTime,
    string CategoryColor,
    string SeverityBand
);
