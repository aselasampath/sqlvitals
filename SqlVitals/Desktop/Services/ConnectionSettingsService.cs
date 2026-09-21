using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Persists SQL Server connection settings to a per-user encrypted file.
/// The connection details are protected with Windows DPAPI (current-user scope)
/// so they are unreadable by other OS accounts.
/// </summary>
public class ConnectionSettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlVitals");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.dat");

    // ── Public API ────────────────────────────────────────────────────────────

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

            ConnectionSettings settings;
            if (!string.IsNullOrWhiteSpace(raw.EncryptedConnection))
            {
                settings = JsonSerializer.Deserialize<ConnectionSettings>(Decrypt(raw.EncryptedConnection))
                           ?? new ConnectionSettings();
            }
            else if (!string.IsNullOrWhiteSpace(raw.EncryptedConnectionString))
            {
                // Settings saved before the connection form existed held a raw connection string.
                settings = ConnectionSettings.FromConnectionString(Decrypt(raw.EncryptedConnectionString));
            }
            else
            {
                settings = new ConnectionSettings();
            }

            settings.CommandTimeoutSeconds = raw.CommandTimeoutSeconds;
            return settings;
        }
        catch
        {
            return new ConnectionSettings();
        }
    }

    /// <summary>Saves settings to disk. Connection details are encrypted before writing.</summary>
    public void Save(ConnectionSettings settings)
    {
        Directory.CreateDirectory(SettingsDir);

        // The password only reaches disk when the user asked for it to be remembered.
        var toStore = settings.Clone();
        if (!toStore.SavePassword)
            toStore.Password = string.Empty;

        var raw = new RawSettings
        {
            EncryptedConnection   = Encrypt(JsonSerializer.Serialize(toStore)),
            CommandTimeoutSeconds = settings.CommandTimeoutSeconds
        };

        var json = JsonSerializer.Serialize(raw, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsFile, json);
    }

    /// <summary>Returns true when a settings file with a server already exists.</summary>
    public bool HasSavedSettings() => Load().IsConfigured;

    // ── DPAPI helpers ─────────────────────────────────────────────────────────

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

    // ── Internal serialisation model ──────────────────────────────────────────

    private sealed class RawSettings
    {
        public string EncryptedConnection       { get; set; } = string.Empty;

        // Legacy: read on load, never written.
        public string EncryptedConnectionString { get; set; } = string.Empty;

        public int    CommandTimeoutSeconds     { get; set; } = 30;
    }
}

public enum SqlAuthMode
{
    SqlServer,
    EntraMfa,
    Windows,
}

public enum EncryptMode
{
    Mandatory,
    Optional,
    Strict,
}

/// <summary>Decrypted, in-memory view of the user's connection settings.</summary>
public class ConnectionSettings
{
    public string      Server                 { get; set; } = string.Empty;
    public SqlAuthMode Authentication         { get; set; } = SqlAuthMode.SqlServer;
    public string      UserName               { get; set; } = string.Empty;
    public string      Password               { get; set; } = string.Empty;
    public bool        SavePassword           { get; set; }
    public string      Database               { get; set; } = string.Empty;
    public EncryptMode Encrypt                { get; set; } = EncryptMode.Mandatory;
    public bool        TrustServerCertificate { get; set; }
    public int         ConnectTimeoutSeconds  { get; set; } = 30;
    public string      AdditionalParameters   { get; set; } = string.Empty;

    [System.Text.Json.Serialization.JsonIgnore]
    public int CommandTimeoutSeconds { get; set; } = 30;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Server);

    /// <summary>
    /// True when a SQL login has no password to hand — e.g. "Remember password" was off
    /// in the previous session — so connecting would fail until the user supplies one.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool NeedsPassword => Authentication == SqlAuthMode.SqlServer && string.IsNullOrEmpty(Password);

    [System.Text.Json.Serialization.JsonIgnore]
    public string ConnectionString => IsConfigured ? BuildConnectionString() : string.Empty;

    /// <summary>Builds the ADO.NET connection string, optionally targeting a different database.</summary>
    public string BuildConnectionString(string? databaseOverride = null)
    {
        // Start from the extra parameters so the form fields always win over them.
        var csb = new SqlConnectionStringBuilder(AdditionalParameters?.Trim() ?? string.Empty)
        {
            DataSource             = Server.Trim(),
            Encrypt                = this.Encrypt switch
            {
                EncryptMode.Optional => SqlConnectionEncryptOption.Optional,
                EncryptMode.Strict   => SqlConnectionEncryptOption.Strict,
                _                    => SqlConnectionEncryptOption.Mandatory,
            },
            TrustServerCertificate = this.TrustServerCertificate,
            ConnectTimeout         = ConnectTimeoutSeconds > 0 ? ConnectTimeoutSeconds : 30,
        };

        var database = databaseOverride ?? Database;
        if (!string.IsNullOrWhiteSpace(database))
            csb.InitialCatalog = database.Trim();

        switch (Authentication)
        {
            case SqlAuthMode.SqlServer:
                csb.Authentication = SqlAuthenticationMethod.SqlPassword;
                csb.UserID         = UserName.Trim();
                csb.Password       = Password;
                break;

            case SqlAuthMode.EntraMfa:
                // Opens the Microsoft sign-in prompt on first connect. SqlClient caches the
                // token for the life of the process, so later connections don't prompt again.
                csb.Authentication = SqlAuthenticationMethod.ActiveDirectoryInteractive;
                if (!string.IsNullOrWhiteSpace(UserName))
                    csb.UserID = UserName.Trim();   // login hint (user@domain)
                break;

            case SqlAuthMode.Windows:
                csb.IntegratedSecurity = true;
                break;
        }

        return csb.ConnectionString;
    }

    /// <summary>Maps an existing connection string onto the form fields.</summary>
    public static ConnectionSettings FromConnectionString(string connectionString)
    {
        var csb = new SqlConnectionStringBuilder(connectionString);

        var settings = new ConnectionSettings
        {
            Server                 = csb.DataSource,
            Database               = csb.InitialCatalog,
            UserName               = csb.UserID,
            Password               = csb.Password,
            SavePassword           = !string.IsNullOrEmpty(csb.Password),
            TrustServerCertificate = csb.TrustServerCertificate,
            ConnectTimeoutSeconds  = csb.ConnectTimeout,
            Encrypt                = csb.Encrypt == SqlConnectionEncryptOption.Strict   ? EncryptMode.Strict
                                   : csb.Encrypt == SqlConnectionEncryptOption.Optional ? EncryptMode.Optional
                                   : EncryptMode.Mandatory,
            Authentication         = csb.IntegratedSecurity ? SqlAuthMode.Windows
                                   : csb.Authentication == SqlAuthenticationMethod.ActiveDirectoryInteractive
                                                        ? SqlAuthMode.EntraMfa
                                   : SqlAuthMode.SqlServer,
        };

        // Carry anything the form has no field for (e.g. MultipleActiveResultSets) as-is.
        foreach (var key in new[]
                 {
                     "Data Source", "Initial Catalog", "User ID", "Password", "Trust Server Certificate",
                     "Connect Timeout", "Encrypt", "Integrated Security", "Authentication",
                 })
            csb.Remove(key);
        settings.AdditionalParameters = csb.ConnectionString;

        return settings;
    }

    public ConnectionSettings Clone() => (ConnectionSettings)MemberwiseClone();
}
