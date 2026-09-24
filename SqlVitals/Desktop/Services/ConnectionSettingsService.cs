using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Regressions;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Persists the user's saved SQL Server connections to a per-user encrypted file.
/// The connection details are protected with Windows DPAPI (current-user scope)
/// so they are unreadable by other OS accounts.
/// </summary>
public class ConnectionSettingsService
{
    private static readonly string SettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlVitals");

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.dat");

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // Passwords for connections without "Remember password", held for this process only so
    // switching back to such a connection doesn't ask for the password again.
    private readonly Dictionary<Guid, string> _sessionPasswords = new();

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Loads the saved connections from disk. Returns an empty store when no file exists.</summary>
    public ConnectionStore Load()
    {
        var store = ReadFile();

        foreach (var conn in store.Connections)
        {
            conn.CommandTimeoutSeconds = store.CommandTimeoutSeconds;
            if (conn.HealthThresholds is not null)
                conn.HealthThresholds = HealthThresholds.Normalize(conn.HealthThresholds);
            if (string.IsNullOrEmpty(conn.Password) && _sessionPasswords.TryGetValue(conn.Id, out var pwd))
                conn.Password = pwd;
        }

        // Never point at a connection that no longer exists.
        if (store.ActiveConnectionId is { } id && store.Connections.All(c => c.Id != id))
            store.ActiveConnectionId = null;

        return store;
    }

    /// <summary>Adds the connection, or replaces the saved one with the same <see cref="ConnectionSettings.Id"/>.</summary>
    public void Upsert(ConnectionSettings settings)
    {
        var store = Load();
        var index = store.Connections.FindIndex(c => c.Id == settings.Id);
        if (index >= 0)
            store.Connections[index] = settings.Clone();
        else
            store.Connections.Add(settings.Clone());

        store.CommandTimeoutSeconds = settings.CommandTimeoutSeconds;

        if (settings.SavePassword || string.IsNullOrEmpty(settings.Password))
            _sessionPasswords.Remove(settings.Id);
        else
            _sessionPasswords[settings.Id] = settings.Password;

        Save(store);
    }

    /// <summary>Removes a saved connection. Clears the active connection when it was the one removed.</summary>
    public void Remove(Guid id)
    {
        var store = Load();
        store.Connections.RemoveAll(c => c.Id == id);
        if (store.ActiveConnectionId == id)
            store.ActiveConnectionId = null;

        _sessionPasswords.Remove(id);
        Save(store);
    }

    /// <summary>Saves how often history detail snapshots are taken and how long history is kept.</summary>
    public void SetHistorySettings(int intervalMinutes, int retentionDays)
    {
        var store = Load();
        store.HistoryIntervalMinutes = HistorySettings.NormalizeInterval(intervalMinutes);
        store.HistoryRetentionDays   = HistorySettings.NormalizeRetention(retentionDays);
        Save(store);
    }

    /// <summary>Saves when the Query Regressions page flags a query (see <see cref="RegressionCriteria"/>).</summary>
    public void SetRegressionSettings(RegressionCriteria criteria)
    {
        var store = Load();
        store.RegressionThresholdPct  = RegressionCriteria.NormalizeThreshold(criteria.ThresholdPct);
        store.RegressionMinExecutions = RegressionCriteria.NormalizeMinExecutions(criteria.MinExecutions);
        Save(store);
    }

    /// <summary>Saves the health-dot thresholds used by every connection without its own.</summary>
    public void SetHealthThresholds(HealthThresholds thresholds)
    {
        var store = Load();
        store.HealthThresholds = HealthThresholds.Normalize(thresholds);
        Save(store);
    }

    /// <summary>Saves how many samples in a row start and end an alert (see <see cref="AlertSettings"/>).</summary>
    public void SetAlertSettings(int samples)
    {
        var store = Load();
        store.AlertSamples = AlertSettings.NormalizeSamples(samples);
        Save(store);
    }

    /// <summary>Marks a saved connection as the one the dashboard uses; null leaves none active.</summary>
    public void SetActive(Guid? id)
    {
        var store = Load();
        store.ActiveConnectionId = id is { } value && store.Connections.Any(c => c.Id == value) ? value : null;
        Save(store);
    }

    /// <summary>
    /// Masks password values in a message before it is shown, in case a driver or parser
    /// error echoes part of a connection string back.
    /// </summary>
    public static string RedactSecrets(string message) =>
        System.Text.RegularExpressions.Regex.Replace(
            message ?? string.Empty,
            @"(?i)\b(password|pwd)(\s*=\s*)(""[^""]*""|'[^']*'|[^;""']*)",
            "$1$2*****");

    // ── File I/O ──────────────────────────────────────────────────────────────

    private static ConnectionStore ReadFile()
    {
        if (!File.Exists(SettingsFile))
            return new ConnectionStore();

        try
        {
            var raw = JsonSerializer.Deserialize<RawSettings>(File.ReadAllText(SettingsFile));
            if (raw is null) return new ConnectionStore();

            var store = new ConnectionStore
            {
                CommandTimeoutSeconds  = raw.CommandTimeoutSeconds > 0 ? raw.CommandTimeoutSeconds : 30,
                ActiveConnectionId     = raw.ActiveConnectionId,
                HistoryIntervalMinutes = HistorySettings.NormalizeInterval(raw.HistoryIntervalMinutes),
                HistoryRetentionDays   = HistorySettings.NormalizeRetention(raw.HistoryRetentionDays),
                RegressionThresholdPct  = RegressionCriteria.NormalizeThreshold(raw.RegressionThresholdPct),
                RegressionMinExecutions = RegressionCriteria.NormalizeMinExecutions(raw.RegressionMinExecutions),
                HealthThresholds        = HealthThresholds.Normalize(raw.HealthThresholds),
                AlertSamples            = AlertSettings.NormalizeSamples(raw.AlertSamples),
            };

            if (!string.IsNullOrWhiteSpace(raw.EncryptedConnections))
            {
                store.Connections = JsonSerializer.Deserialize<List<ConnectionSettings>>(Decrypt(raw.EncryptedConnections))
                                    ?? new List<ConnectionSettings>();
            }
            else
            {
                // Files written before multiple connections held a single one; carry it over as
                // the active connection so upgrading doesn't lose it.
                ConnectionSettings? legacy = null;
                if (!string.IsNullOrWhiteSpace(raw.EncryptedConnection))
                    legacy = JsonSerializer.Deserialize<ConnectionSettings>(Decrypt(raw.EncryptedConnection));
                else if (!string.IsNullOrWhiteSpace(raw.EncryptedConnectionString))
                    // Older still: a raw connection string from before the connection form existed.
                    legacy = ConnectionSettings.FromConnectionString(Decrypt(raw.EncryptedConnectionString));

                if (legacy is { IsConfigured: true })
                {
                    legacy.Id = Guid.NewGuid();
                    store.Connections.Add(legacy);
                    store.ActiveConnectionId = legacy.Id;
                }

                // Write the new format straight away: the Id is generated here, so leaving the
                // old file in place would hand out a different Id on every load.
                try { Save(store); } catch { /* retried on the next load */ }
            }

            // Guard against hand-edited files; persist so the Ids stay stable.
            var missingIds = store.Connections.Where(c => c.Id == Guid.Empty).ToList();
            foreach (var conn in missingIds)
                conn.Id = Guid.NewGuid();
            if (missingIds.Count > 0)
                try { Save(store); } catch { /* retried on the next load */ }

            return store;
        }
        catch
        {
            return new ConnectionStore();
        }
    }

    private static void Save(ConnectionStore store)
    {
        Directory.CreateDirectory(SettingsDir);

        // A password only reaches disk when the user asked for it to be remembered.
        var toStore = store.Connections.Select(c =>
        {
            var copy = c.Clone();
            if (!copy.SavePassword)
                copy.Password = string.Empty;
            return copy;
        }).ToList();

        var raw = new RawSettings
        {
            EncryptedConnections   = Encrypt(JsonSerializer.Serialize(toStore)),
            ActiveConnectionId     = store.ActiveConnectionId,
            CommandTimeoutSeconds  = store.CommandTimeoutSeconds,
            HistoryIntervalMinutes = store.HistoryIntervalMinutes,
            HistoryRetentionDays   = store.HistoryRetentionDays,
            RegressionThresholdPct  = store.RegressionThresholdPct,
            RegressionMinExecutions = store.RegressionMinExecutions,
            HealthThresholds        = store.HealthThresholds,
            AlertSamples            = store.AlertSamples,
        };

        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(raw, FileJsonOptions));
    }

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
        public string? EncryptedConnections      { get; set; }
        public Guid?   ActiveConnectionId        { get; set; }
        public int     CommandTimeoutSeconds     { get; set; } = 30;

        // Missing from files written before these settings existed: the defaults apply.
        public int     HistoryIntervalMinutes    { get; set; } = HistorySettings.DefaultIntervalMinutes;
        public int     HistoryRetentionDays      { get; set; } = HistorySettings.DefaultRetentionDays;
        public double  RegressionThresholdPct    { get; set; } = RegressionCriteria.DefaultThresholdPct;
        public long    RegressionMinExecutions   { get; set; } = RegressionCriteria.DefaultMinExecutions;
        public HealthThresholds? HealthThresholds { get; set; }
        public int     AlertSamples              { get; set; } = AlertSettings.DefaultSamples;

        // Legacy single-connection formats: read on load, never written.
        public string? EncryptedConnection       { get; set; }
        public string? EncryptedConnectionString { get; set; }
    }
}

/// <summary>Decrypted, in-memory view of everything in the settings file.</summary>
public class ConnectionStore
{
    public List<ConnectionSettings> Connections           { get; set; } = new();
    public Guid?                    ActiveConnectionId    { get; set; }
    public int                      CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>Minutes between monitoring-history detail snapshots; one of <see cref="HistorySettings.IntervalChoicesMinutes"/>.</summary>
    public int HistoryIntervalMinutes { get; set; } = HistorySettings.DefaultIntervalMinutes;

    /// <summary>Days of monitoring history kept; one of <see cref="HistorySettings.RetentionChoicesDays"/>.</summary>
    public int HistoryRetentionDays   { get; set; } = HistorySettings.DefaultRetentionDays;

    /// <summary>Percent rise in average duration or CPU that the Query Regressions page flags.</summary>
    public double RegressionThresholdPct  { get; set; } = RegressionCriteria.DefaultThresholdPct;

    /// <summary>Executions a query needs in each period before the Query Regressions page compares it.</summary>
    public long   RegressionMinExecutions { get; set; } = RegressionCriteria.DefaultMinExecutions;

    /// <summary>The saved Query Regressions criteria.</summary>
    public RegressionCriteria RegressionCriteria => new(RegressionThresholdPct, RegressionMinExecutions);

    /// <summary>Health-dot thresholds for every connection that doesn't override them.</summary>
    public HealthThresholds HealthThresholds { get; set; } = HealthThresholds.Default;

    /// <summary>The thresholds a connection's health dot is graded on: its own, or the saved ones.</summary>
    public HealthThresholds ThresholdsFor(ConnectionSettings connection) =>
        connection.HealthThresholds ?? HealthThresholds;

    /// <summary>
    /// Samples in a row an indicator must stay past a threshold for an alert to start, and back
    /// to normal for it to end; one of <see cref="AlertSettings.SampleChoices"/>.
    /// </summary>
    public int AlertSamples { get; set; } = AlertSettings.DefaultSamples;

    public ConnectionSettings? Active =>
        ActiveConnectionId is { } id ? Connections.FirstOrDefault(c => c.Id == id) : null;
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

/// <summary>Decrypted, in-memory view of one saved connection.</summary>
public class ConnectionSettings
{
    public Guid        Id                     { get; set; } = Guid.NewGuid();

    /// <summary>User-chosen label for the connection selector. Blank means use <see cref="DefaultName"/>.</summary>
    public string      Name                   { get; set; } = string.Empty;

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

    /// <summary>Keep collecting live metrics for this connection while another one is active.</summary>
    public bool        MonitorInBackground    { get; set; } = true;

    /// <summary>This connection's own health-dot thresholds; null uses the ones in Settings.</summary>
    public HealthThresholds? HealthThresholds { get; set; }

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

    // ── Display (never includes credentials) ──────────────────────────────────

    [System.Text.Json.Serialization.JsonIgnore]
    public string DefaultName => string.IsNullOrWhiteSpace(Database) ? Server.Trim() : $"{Server.Trim()} / {Database.Trim()}";

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? DefaultName : Name.Trim();

    [System.Text.Json.Serialization.JsonIgnore]
    public string DatabaseLabel => string.IsNullOrWhiteSpace(Database) ? "(default database)" : Database.Trim();

    [System.Text.Json.Serialization.JsonIgnore]
    public string AuthenticationLabel => Authentication switch
    {
        SqlAuthMode.EntraMfa => "Entra MFA",
        SqlAuthMode.Windows  => "Windows",
        _                    => "SQL login",
    };

    /// <summary>One-line summary for lists and tooltips, e.g. "myserver · Sales · SQL login".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary => $"{Server.Trim()} · {DatabaseLabel} · {AuthenticationLabel}";

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
