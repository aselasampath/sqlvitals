using System.Data;
using SqlVitals.Engine.ConfigChecks;
using SqlVitals.Engine.Errors;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// Reads what the configuration checks (#42) look at: three sp_configure options, the server's
/// processors and memory, TempDB's data files and each database's options. Read only. The
/// processors and memory need VIEW SERVER STATE; without it the rest is still checked.
/// </summary>
public class ConfigurationRepository(SqlTarget target, ServerCapabilitiesCache? capabilities = null) : BaseRepository(target, capabilities), IConfigurationRepository
{
    internal static readonly string ServerSql = SqlScripts.Get("Configuration/Server");

    // value_in_use is what the server runs with; value differs until RECONFIGURE.
    internal static readonly string OptionsSql = SqlScripts.Get("Configuration/Options");

    // Snapshots take their options from their source database.
    internal static readonly string DatabasesSql = SqlScripts.Get("Configuration/Databases");

    // Visible online schedulers are the processors SQL Server can use, after affinity and the
    // edition's limits. sys.dm_os_nodes has soft-NUMA's nodes; node 64 is the DAC's.
    internal static readonly string HardwareSql = SqlScripts.Get("Configuration/Hardware");

    // tempdb.sys.database_files has the files' current sizes (sys.master_files has the sizes
    // TempDB starts at). Every login can read it: guest is enabled in tempdb.
    internal static readonly string TempDbSql = SqlScripts.Get("Configuration/TempDb");

    // Azure SQL Database sets MAXDOP per database.
    internal static readonly string DatabaseMaxDopSql = SqlScripts.Get("Configuration/DatabaseMaxDop");

    public async Task<ConfigCheckList> GetConfigurationChecksAsync(CancellationToken cancellationToken = default)
    {
        using var conn = CreateConnection();

        int edition = (await CapabilitiesAsync(cancellationToken)).EngineEdition;
        var server  = await QFirst(conn, ServerSql, cancellationToken);
        var version = (string?)server?.ProductVersion ?? string.Empty;
        var isAzure = edition == ServerConfiguration.AzureSqlDatabaseEdition;

        var databases = (await Q(conn, DatabasesSql, cancellationToken)).Select(r => new DatabaseOptions(
            (string)r.Name,
            Convert.ToInt32(r.CompatibilityLevel),
            Convert.ToInt32(r.AutoShrink) == 1,
            (string)r.PageVerify,
            Convert.ToInt32(r.IsCurrent) == 1)).ToList();

        if (isAzure)
        {
            int? maxDop = null;
            try
            {
                var row = await QFirst(conn, DatabaseMaxDopSql, cancellationToken);
                maxDop = row is null ? null : Convert.ToInt32(row.Value);
            }
            catch (WaitStatsException)
            {
                // Checked as "couldn't be read".
            }
            return ConfigurationChecks.Run(new ServerConfiguration(edition, version, null, null, null, null, databases, null, maxDop));
        }

        var options = (await Q(conn, OptionsSql, cancellationToken)).ToDictionary(
            r => (string)r.Name,
            r => new ConfigOption(Convert.ToInt64(r.Value), Convert.ToInt64(r.ValueInUse)),
            StringComparer.OrdinalIgnoreCase);

        var config = new ServerConfiguration(
            edition,
            version,
            options.GetValueOrDefault("max degree of parallelism"),
            options.GetValueOrDefault("cost threshold for parallelism"),
            options.GetValueOrDefault("max server memory (MB)"),
            await ReadHardwareAsync(conn, cancellationToken: cancellationToken),
            databases,
            await ReadTempDbAsync(conn, cancellationToken: cancellationToken));
        return ConfigurationChecks.Run(config);
    }

    private async Task<ServerHardware?> ReadHardwareAsync(IDbConnection conn, CancellationToken cancellationToken)
    {
        try
        {
            var r = await QFirst(conn, HardwareSql, cancellationToken);
            if (r is null)
                return null;
            int processors = r.LogicalProcessors is null ? 0 : Convert.ToInt32(r.LogicalProcessors);
            if (processors == 0)
                return null;
            return new ServerHardware(
                processors,
                Convert.ToInt32(r.NumaNodes ?? 1),
                Convert.ToInt32(r.ProcessorsPerNumaNode ?? processors),
                Convert.ToInt64(r.PhysicalMemoryMb ?? 0L));
        }
        catch (WaitStatsException ex) when (ex.Failure.IsPermission)
        {
            return null;
        }
    }

    // Not needed for the other checks, so a TempDB that can't be read leaves only its own two out.
    private async Task<IReadOnlyList<TempDbDataFile>?> ReadTempDbAsync(IDbConnection conn, CancellationToken cancellationToken)
    {
        try
        {
            return (await Q(conn, TempDbSql, cancellationToken)).Select(r => new TempDbDataFile(
                (string)r.Name,
                (string?)r.PhysicalName ?? string.Empty,
                Convert.ToInt64(r.SizeMb),
                Convert.ToInt32(r.IsPercentGrowth) == 1,
                Convert.ToInt64(r.Growth))).ToList();
        }
        catch (WaitStatsException)
        {
            return null;
        }
    }
}
