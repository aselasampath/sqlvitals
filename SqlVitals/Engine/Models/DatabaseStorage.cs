namespace SqlVitals.Engine.Models;

/// <summary>A single SQL Server sp_configure parameter.</summary>
public record ServerConfigParam(
    string  ParameterName,
    long    CurrentValue,
    long    MinValue,
    long    MaxValue,
    string  Description,
    bool    IsDynamic,
    bool    IsAdvanced
);

/// <summary>One database file (data / log / filestream) with size info.</summary>
public record DatabaseFile(
    string   DatabaseName,
    string   State,
    string   RecoveryModel,
    int      CompatibilityLevel,
    string?  Collation,
    bool     AutoShrink,
    bool     AutoCreateStats,
    bool     AutoUpdateStats,
    bool     QueryStoreOn,
    bool     CDCEnabled,
    bool     Encrypted,
    string   FileType,          // ROWS / LOG / FILESTREAM / FULLTEXT
    string   LogicalFileName,
    string   PhysicalPath,
    decimal  FileSizeMB,
    decimal  SpaceUsedMB,
    decimal  FreeSpaceMB,
    decimal  MaxSizeMB,         // -1 = unlimited
    string   AutoGrowth
);

/// <summary>A quick TempDB file size row.</summary>
public record TempDbFileSummary(
    string  FileName,
    string  FileType,
    string  PhysicalPath,
    decimal SizeMB,
    decimal UsedMB,
    decimal FreeMB
);

/// <summary>Storage footprint for a single user table.</summary>
public record TableStorage(
    string  SchemaName,
    string  TableName,
    long    RowCount,
    decimal DataSizeMB,
    decimal IndexSizeMB,
    decimal TotalSizeMB,
    decimal UsedSizeMB,
    decimal UnusedSizeMB,
    int     IndexCount,
    string  TableType         // Heap / Clustered
);
