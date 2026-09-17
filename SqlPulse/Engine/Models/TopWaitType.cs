namespace SqlPulse.Engine.Models;

public record TopWaitType(
    int WaitRank,
    string WaitType,
    string WaitCategory,
    long WaitingTasksCount,
    long WaitTimeMs,
    long MaxWaitTimeMs,
    long SignalWaitTimeMs,
    long ResourceWaitTimeMs,
    double SignalWaitPct,
    double WaitTimeSec,
    double AvgWaitMsPerTask,
    double PctOfTotal,
    double CumulativePct,
    DateTime CaptureTime,
    string CategoryColor,
    string RankLabel
);
