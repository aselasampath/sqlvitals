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
}
