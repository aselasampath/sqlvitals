namespace SqlVitals.Engine.Models;

/// <summary>Statistics that have not been updated in a while.</summary>
/// <param name="LastUpdated">Null when the statistic has never been updated (built on an empty table); so are the next three.</param>
public record StaleStatistic(
    string    SchemaName,
    string    TableName,
    string    StatisticsName,
    DateTime? LastUpdated,
    int?      DaysOld,
    long?     RowsInTable,
    long?     RowsSampled,
    double    SamplePct,
    long      ModificationsSinceLastUpdate,
    bool      IsIncremental,
    bool      AutoCreated,
    bool      UserCreated,
    string    DatabaseName,
    DateTime  CaptureTime
);
