namespace SqlPulse.Engine.Models;

/// <summary>Index usage stats from dm_db_index_usage_stats.</summary>
public record IndexUsagePattern(
    string    TableName,
    string?   IndexName,
    string    IndexType,
    long      UserSeeks,
    long      UserScans,
    long      UserLookups,
    long      UserUpdates,
    DateTime? LastUserSeek,
    DateTime? LastUserScan,
    DateTime? LastUserLookup,
    DateTime? LastUserUpdate,
    string    IndexHealth,   // Healthy / Mostly Scanned - Review / Unused
    string    DatabaseName,
    DateTime  CaptureTime
);
