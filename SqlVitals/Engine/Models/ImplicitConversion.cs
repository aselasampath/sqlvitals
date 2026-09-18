namespace SqlVitals.Engine.Models;

/// <summary>A cached plan that contains CONVERT_IMPLICIT operations.</summary>
public record ImplicitConversion(
    string?  DatabaseName,
    string?  ObjectName,
    int      ExecutionCount,
    long     PlanSizeKB,
    string?  QueryText,
    DateTime CaptureTime
);
