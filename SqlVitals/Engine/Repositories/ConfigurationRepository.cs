using System.Data;
using Microsoft.Extensions.Configuration;
using SqlVitals.Engine.ConfigChecks;
using SqlVitals.Engine.Errors;

namespace SqlVitals.Engine.Repositories;

/// <summary>
/// Reads what the configuration checks (#42) look at: three sp_configure options, the server's
/// processors and memory, TempDB's data files and each database's options. Read only. The
/// processors and memory need VIEW SERVER STATE; without it the rest is still checked.
/// </summary>
public class ConfigurationRepository(IConfiguration configuration) : BaseRepository(configuration), IConfigurationRepository
{
    // VIEW SERVER STATE (VIEW SERVER PERFORMANCE STATE on SQL Server 2022) is missing.
    private static readonly int[] PermissionErrors = [297, 300];

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

    public async Task<ConfigCheckList> GetConfigurationChecksAsync()
    {
        using var conn = CreateConnection();

        var server  = await QFirst(conn, ServerSql);
        int edition = server is null ? 0 : Convert.ToInt32(server.Edition);
        var version = (string?)server?.ProductVersion ?? string.Empty;
        var isAzure = edition == ServerConfiguration.AzureSqlDatabaseEdition;

        var databases = (await Q(conn, DatabasesSql)).Select(r => new DatabaseOptions(
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
                var row = await QFirst(conn, DatabaseMaxDopSql);
                maxDop = row is null ? null : Convert.ToInt32(row.Value);
            }
            catch (WaitStatsException)
            {
                // Checked as "couldn't be read".
            }
            return ConfigurationChecks.Run(new ServerConfiguration(edition, version, null, null, null, null, databases, null, maxDop));
        }

        var options = (await Q(conn, OptionsSql)).ToDictionary(
            r => (string)r.Name,
            r => new ConfigOption(Convert.ToInt64(r.Value), Convert.ToInt64(r.ValueInUse)),
            StringComparer.OrdinalIgnoreCase);

        var config = new ServerConfiguration(
            edition,
            version,
            options.GetValueOrDefault("max degree of parallelism"),
            options.GetValueOrDefault("cost threshold for parallelism"),
            options.GetValueOrDefault("max server memory (MB)"),
            await ReadHardwareAsync(conn),
            databases,
            await ReadTempDbAsync(conn));
        return ConfigurationChecks.Run(config);
    }

    private async Task<ServerHardware?> ReadHardwareAsync(IDbConnection conn)
    {
        try
        {
            var r = await QFirst(conn, HardwareSql);
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
        catch (WaitStatsException ex) when (PermissionErrors.Contains(ex.SqlErrorNumber))
        {
            return null;
        }
    }

    // Not needed for the other checks, so a TempDB that can't be read leaves only its own two out.
    private async Task<IReadOnlyList<TempDbDataFile>?> ReadTempDbAsync(IDbConnection conn)
    {
        try
        {
            return (await Q(conn, TempDbSql)).Select(r => new TempDbDataFile(
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
