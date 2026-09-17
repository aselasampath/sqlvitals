namespace SqlPulse.Engine.Models;

/// <summary>Index fragmentation data from dm_db_index_physical_stats.</summary>
public record IndexFragmentation(
    string   TableName,
    string?  IndexName,
    string   IndexType,
    double   FragmentationPercent,
    long     PageCount,
    double   AvgPageSpaceUsed,
    long     RecordCount,
    string   DatabaseName,
    string   RecommendedAction,   // No Action Needed / Reorganize / Rebuild
    DateTime CaptureTime
);
