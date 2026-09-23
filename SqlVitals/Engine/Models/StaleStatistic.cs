namespace SqlVitals.Engine.Models;

/// <summary>Statistics that have not been updated in a while.</summary>
public record StaleStatistic(
    string    SchemaName,
    string    TableName,
    string    StatisticsName,
    DateTime? LastUpdated,
    int       DaysOld,
    long      RowsInTable,
    long      RowsSampled,
    double    SamplePct,
    long      ModificationsSinceLastUpdate,
    bool      IsIncremental,
    bool      AutoCreated,
    bool      UserCreated,
    string    DatabaseName,
    DateTime  CaptureTime
);
