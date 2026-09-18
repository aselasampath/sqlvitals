namespace SqlVitals.Engine.Models;

/// <summary>A missing index suggestion from the DMV.</summary>
public record MissingIndex(
    string   DatabaseName,
    string   SchemaName,
    string   TableName,
    string?  EqualityColumns,
    string?  InequalityColumns,
    string?  IncludedColumns,
    long     UniqueCompiles,
    long     UserSeeks,
    long     UserScans,
    DateTime? LastUserSeek,
    DateTime? LastUserScan,
    double   AvgTotalUserCost,
    double   AvgUserImpact,
    double   ImpactScore,
    string   Severity,           // CRITICAL / WARNING / INFO
    string   CreateIndexStatement,
    DateTime CaptureTime
);

/// <summary>An index that is being maintained but never read.</summary>
public record UnusedIndex(
    string   DatabaseName,
    string   SchemaName,
    string   TableName,
    string   IndexName,
    string   IndexType,
    bool     IsUnique,
    bool     IsPrimaryKey,
    bool     IsUniqueConstraint,
    long     UserSeeks,
    long     UserScans,
    long     UserLookups,
    long     UserUpdates,
    DateTime? LastUserSeek,
    DateTime? LastUserScan,
    DateTime? LastUserUpdate,
    long     TotalReads,
    long     TableRows,
    DateTime CaptureTime
);
