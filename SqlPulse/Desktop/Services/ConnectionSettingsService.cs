using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SqlPulse.Desktop.Services;

/// <summary>
/// Persists SQL Server connection settings to a per-user encrypted file.
/// The connection string is protected with Windows DPAPI (current-user scope)
/// so it is unreadable by other OS accounts.
/// </summary>
public class ConnectionSettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlPulse");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.dat");

    // ?? Public API ????????????????????????????????????????????????????????????

    /// <summary>Loads settings from disk. Returns defaults when no file exists.</summary>
    public ConnectionSettings Load()
    {
        if (!File.Exists(SettingsFile))
            return new ConnectionSettings();

        try
        {
            var json = File.ReadAllText(SettingsFile);
            var raw  = JsonSerializer.Deserialize<RawSettings>(json);
            if (raw is null) return new ConnectionSettings();

            var connectionString = string.IsNullOrWhiteSpace(raw.EncryptedConnectionString)
                ? string.Empty
                : Decrypt(raw.EncryptedConnectionString);

            return new ConnectionSettings
            {
                ConnectionString       = connectionString,
                CommandTimeoutSeconds  = raw.CommandTimeoutSeconds
            };
        }
        catch
        {
            return new ConnectionSettings();
        }
    }

    /// <summary>Saves settings to disk. The connection string is encrypted before writing.</summary>
    public void Save(ConnectionSettings settings)
    {
        Directory.CreateDirectory(SettingsDir);

        var raw = new RawSettings
        {
            EncryptedConnectionString = string.IsNullOrWhiteSpace(settings.ConnectionString)
                ? string.Empty
                : Encrypt(settings.ConnectionString),
            CommandTimeoutSeconds = settings.CommandTimeoutSeconds
        };

        var json = JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsFile, json);
    }

    /// <summary>Returns true when a settings file with a connection string already exists.</summary>
    public bool HasSavedSettings()
    {
        var loaded = Load();
        return !string.IsNullOrWhiteSpace(loaded.ConnectionString);
    }

    // ?? DPAPI helpers ?????????????????????????????????????????????????????????

    private static string Encrypt(string plainText)
    {
        var bytes     = Encoding.UTF8.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(encrypted);
    }

    private static string Decrypt(string cipherText)
    {
        var bytes     = Convert.FromBase64String(cipherText);
        var decrypted = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(decrypted);
    }

    // ?? Internal serialisation model ??????????????????????????????????????????

    private sealed class RawSettings
    {
        public string EncryptedConnectionString { get; set; } = string.Empty;
        public int    CommandTimeoutSeconds      { get; set; } = 30;
    }
}

/// <summary>Decrypted, in-memory view of the user's connection settings.</summary>
public class ConnectionSettings
{
    public string ConnectionString      { get; set; } = string.Empty;
    public int    CommandTimeoutSeconds { get; set; } = 30;
}
