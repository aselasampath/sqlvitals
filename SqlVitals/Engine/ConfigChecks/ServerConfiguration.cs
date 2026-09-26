using System.Globalization;

namespace SqlVitals.Engine.ConfigChecks;

/// <summary>An sp_configure option as sys.configurations has it.</summary>
/// <param name="Value">What it is set to.</param>
/// <param name="ValueInUse">What the server is using: differs until RECONFIGURE is run.</param>
public sealed record ConfigOption(long Value, long ValueInUse)
{
    public bool IsPending => Value != ValueInUse;
}

/// <summary>
/// The processors and memory SQL Server has, from sys.dm_os_schedulers, sys.dm_os_nodes and
/// sys.dm_os_sys_info. Needs VIEW SERVER STATE.
/// </summary>
/// <param name="LogicalProcessors">
/// Visible online schedulers: the processors SQL Server can use, after affinity and edition limits.
/// </param>
/// <param name="NumaNodes">NUMA nodes with schedulers, soft-NUMA included; the DAC's node is not.</param>
/// <param name="ProcessorsPerNumaNode">Online schedulers in the largest NUMA node.</param>
public sealed record ServerHardware(int LogicalProcessors, int NumaNodes, int ProcessorsPerNumaNode, long PhysicalMemoryMb);

/// <summary>A database's options from sys.databases.</summary>
/// <param name="PageVerify">page_verify_option_desc: CHECKSUM, TORN_PAGE_DETECTION or NONE.</param>
/// <param name="IsCurrent">The connection's database: the only one checked on Azure SQL Database.</param>
public sealed record DatabaseOptions(string Name, int CompatibilityLevel, bool AutoShrink, string PageVerify, bool IsCurrent = false);

/// <summary>One of TempDB's data files, from tempdb.sys.database_files.</summary>
/// <param name="Growth">Megabytes, or a percentage when <paramref name="IsPercentGrowth"/>. 0 is no autogrowth.</param>
public sealed record TempDbDataFile(string Name, string PhysicalName, long SizeMb, bool IsPercentGrowth, long Growth)
{
    /// <summary>"64 MB", "10%", "none".</summary>
    public string GrowthText => Growth == 0 ? "none" : IsPercentGrowth ? $"{Growth}%" : $"{Growth:N0} MB";
}

/// <summary>
/// What the configuration checks (#42) look at, read in one go. Anything the login couldn't
/// read is null, and the checks that need it say so.
/// </summary>
/// <param name="Edition">SERVERPROPERTY('EngineEdition'): 5 is Azure SQL Database, 8 Managed Instance.</param>
/// <param name="ProductVersion">SERVERPROPERTY('ProductVersion'), "16.0.4135.4".</param>
/// <param name="Hardware">Null without VIEW SERVER STATE, and on Azure SQL Database.</param>
/// <param name="Databases">Every database except snapshots, the checked ones and the rest.</param>
/// <param name="TempDbFiles">Null when TempDB couldn't be read, and on Azure SQL Database.</param>
/// <param name="DatabaseMaxDop">Azure SQL Database only: the MAXDOP database scoped configuration.</param>
public sealed record ServerConfiguration(
    int                            Edition,
    string                         ProductVersion,
    ConfigOption?                  MaxDop,
    ConfigOption?                  CostThreshold,
    ConfigOption?                  MaxServerMemoryMb,
    ServerHardware?                Hardware,
    IReadOnlyList<DatabaseOptions> Databases,
    IReadOnlyList<TempDbDataFile>? TempDbFiles,
    int?                           DatabaseMaxDop = null)
{
    public const int AzureSqlDatabaseEdition = 5;
    public const int ManagedInstanceEdition  = 8;

    public bool IsAzureSqlDatabase => Edition == AzureSqlDatabaseEdition;

    /// <summary>Azure SQL Database and Managed Instance report 12.0 whatever they run: no version to go by.</summary>
    public bool IsVersionless => Edition is AzureSqlDatabaseEdition or ManagedInstanceEdition;

    /// <summary>16 for "16.0.4135.4"; null when it can't be read.</summary>
    public int? ProductMajorVersion =>
        int.TryParse(ProductVersion.Split('.')[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major > 0
            ? major : null;
}
