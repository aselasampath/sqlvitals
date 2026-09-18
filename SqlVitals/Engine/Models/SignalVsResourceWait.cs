namespace SqlVitals.Engine.Models;

public record SignalVsResourceWait(
    long TotalWaitMs,
    long TotalSignalMs,
    long TotalResourceMs,
    double ServerSignalWaitPct,
    double ServerResourceWaitPct,
    string CPUPressureLevel,
    string CPUPressureDescription,
    double TotalWaitSec,
    double TotalSignalSec,
    double TotalResourceSec,
    DateTime ServerStartTime,
    DateTime CaptureTime
);
