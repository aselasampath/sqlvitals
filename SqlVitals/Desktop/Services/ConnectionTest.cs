using Microsoft.Data.SqlClient;

namespace SqlVitals.Desktop.Services;

/// <summary>Test connection, as Settings and an unreachable server's Overview (#130, #143) offer it.</summary>
public static class ConnectionTest
{
    /// <summary>Opens a connection and closes it; null on success, otherwise why not, with any credentials masked.</summary>
    [ReportsItsErrors]
    public static async Task<string?> TryOpenAsync(string connectionString)
    {
        try
        {
            await using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();
            return null;
        }
        catch (Exception ex)
        {
            return ConnectionSettingsService.RedactSecrets(ex.Message);
        }
    }

    /// <summary>
    /// The databases the login can open, by name, for a connection's Database list. Throws when it
    /// can't sign in: the caller says why, redacted.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ListDatabasesAsync(string connectionString) => await Task.Run(async () =>
    {
        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("SELECT name FROM sys.databases WHERE HAS_DBACCESS(name) = 1 ORDER BY name;", conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        var list = new List<string>();
        while (await reader.ReadAsync())
            list.Add(reader.GetString(0));
        return list;
    });
}
