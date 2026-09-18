namespace SqlVitals.Engine.Models;

/// <summary>A single SQL Server performance counter snapshot.</summary>
public record PerfmonCounter(
    string   ObjectName,
    string   CounterName,
    string   InstanceName,
    long     CntrValue,
    int      CntrType,
    DateTime CaptureTime
);
