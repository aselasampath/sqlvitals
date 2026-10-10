using Microsoft.Data.SqlClient;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Service;

/// <summary>Where the SqlVitals Collector service keeps its files, and what Windows calls it (#172).</summary>
public static class CollectorPaths
{
    /// <summary>The service's name for sc.exe and the Services console.</summary>
    public const string ServiceName = "SqlVitalsCollector";

    /// <summary>The name the Services console shows.</summary>
    public const string DisplayName = "SqlVitals Collector";

    /// <summary>
    /// %ProgramData%\SqlVitals\Collector: the machine's, not a user's, since the service runs as its
    /// own account. Install locks it down to the service, the admins and the installing user.
    /// </summary>
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SqlVitals", "Collector");

    /// <summary>The servers and chat channels: collector.json.</summary>
    public static string SettingsFile(string dataDirectory) => Path.Combine(dataDirectory, "collector.json");

    /// <summary>Its own history, the app's schema (<see cref="HistoryStore"/>): history.db.</summary>
    public static string HistoryFile(string dataDirectory) => Path.Combine(dataDirectory, "history.db");

    /// <summary>One log file a day: logs\Collector-yyyyMMdd.log.</summary>
    public static string LogDirectory(string dataDirectory) => Path.Combine(dataDirectory, "logs");
}

/// <summary>How the service signs in to a server (#172). Entra MFA isn't one: nobody is there to sign in.</summary>
public enum CollectorAuth
{
    /// <summary>As the service's account: no secret is saved. The recommended choice.</summary>
    Windows,

    /// <summary>A SQL login, its password encrypted for this machine (see <see cref="ISecretProtector"/>).</summary>
    SqlLogin,
}

/// <summary>The connection's encryption, as SqlClient's Encrypt keyword.</summary>
public enum CollectorEncrypt
{
    Mandatory,
    Optional,
    Strict,
}

/// <summary>
/// A server the collector service watches (#172). Its <see cref="Id"/> is the app's connection id
/// when the app handed it over, so both read the same server's history under one id.
///
/// A class rather than a record, as <see cref="ChatChannel"/> is: a record's generated
/// <see cref="object.ToString"/> would print the password.
/// </summary>
public sealed class CollectorServer
{
    /// <summary>The program name the service's sessions show on the server: told apart from the app's.</summary>
    public const string ApplicationName = "SqlVitals Collector";

    [System.Text.Json.Serialization.JsonIgnore]
    public Guid             Id                     { get; set; } = Guid.NewGuid();

    // The file's "id": read or written only through here, so a server typed in without one is
    // told apart from one with its own (see CollectorSettings.Normalize).
    [System.Text.Json.Serialization.JsonInclude]
    [System.Text.Json.Serialization.JsonPropertyName("id")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "Read and written by System.Text.Json.")]
    private Guid FileId
    {
        get => Id;
        set
        {
            Id     = value;
            HasFileId = value != Guid.Empty;
        }
    }

    [System.Text.Json.Serialization.JsonIgnore]
    internal bool HasFileId { get; private set; }

    /// <summary>What it's called in chat messages and the history; blank uses <see cref="DefaultName"/>.</summary>
    public string           Name                   { get; set; } = string.Empty;

    public string           Server                 { get; set; } = string.Empty;
    public string           Database               { get; set; } = string.Empty;
    public CollectorAuth    Authentication         { get; set; } = CollectorAuth.Windows;
    public string           UserName               { get; set; } = string.Empty;

    /// <summary>
    /// The SQL login's password, in memory. On disk it is only ever the protected form (see
    /// <see cref="CollectorSettingsFile"/>).
    /// </summary>
    public string           Password               { get; set; } = string.Empty;

    public CollectorEncrypt Encrypt                { get; set; } = CollectorEncrypt.Mandatory;
    public bool             TrustServerCertificate { get; set; }
    public int              ConnectTimeoutSeconds  { get; set; } = 30;

    /// <summary>More connection string keywords, e.g. MultiSubnetFailover=True; the fields above win over them.</summary>
    public string           AdditionalParameters   { get; set; } = string.Empty;

    /// <summary>Its own health thresholds; null uses the settings' ones.</summary>
    public HealthThresholds? HealthThresholds      { get; set; }

    /// <summary>The saved password couldn't be decrypted on this machine: it must be set again.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool PasswordUnreadable { get; internal set; }

    // The unreadable password as the file has it: saved back unchanged until a new one is set,
    // so a save for something else doesn't lose it.
    [System.Text.Json.Serialization.JsonIgnore]
    internal string? KeptPassword { get; set; }

    /// <summary>Why it can't be watched as the file has it (extra parameters that aren't a connection string); null when it can.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? Unusable { get; internal set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string DefaultName => string.IsNullOrWhiteSpace(Database) ? Server.Trim() : $"{Server.Trim()} / {Database.Trim()}";

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? DefaultName : Name.Trim();

    [System.Text.Json.Serialization.JsonIgnore]
    public string DatabaseLabel => string.IsNullOrWhiteSpace(Database) ? "(default database)" : Database.Trim();

    /// <summary>A SQL login without a password: it can't be watched until one is set.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool NeedsPassword => Authentication == CollectorAuth.SqlLogin && string.IsNullOrEmpty(Password);

    /// <summary>One line for the command line's list: "sql01 · Sales · Windows". Never a secret.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Summary => $"{Server.Trim()} · {DatabaseLabel} · " +
                             (Authentication == CollectorAuth.Windows ? "Windows" : $"SQL login {UserName.Trim()}");

    /// <summary>The ADO.NET connection string, password included: never log it.</summary>
    public string BuildConnectionString()
    {
        var csb = new SqlConnectionStringBuilder(AdditionalParameters?.Trim() ?? string.Empty)
        {
            DataSource             = Server.Trim(),
            Encrypt                = Encrypt switch
            {
                CollectorEncrypt.Optional => SqlConnectionEncryptOption.Optional,
                CollectorEncrypt.Strict   => SqlConnectionEncryptOption.Strict,
                _                         => SqlConnectionEncryptOption.Mandatory,
            },
            TrustServerCertificate = TrustServerCertificate,
            ConnectTimeout         = ConnectTimeoutSeconds > 0 ? ConnectTimeoutSeconds : 30,
            ApplicationName        = ApplicationName,
        };
        if (!string.IsNullOrWhiteSpace(Database))
            csb.InitialCatalog = Database.Trim();

        if (Authentication == CollectorAuth.SqlLogin)
        {
            csb.Authentication = SqlAuthenticationMethod.SqlPassword;
            csb.UserID         = UserName.Trim();
            csb.Password       = Password;
        }
        else
        {
            csb.IntegratedSecurity = true;
        }
        return csb.ConnectionString;
    }

    /// <summary>Everything its repositories are built from: a change means its collector must be started again.</summary>
    public string Fingerprint(int commandTimeoutSeconds) => $"{Id}|{BuildConnectionString()}|{commandTimeoutSeconds}";

    /// <summary>The server as monitoring reads it: lock timeout of <see cref="SqlTarget.MonitoringLockTimeoutMilliseconds"/>, as the app's monitoring.</summary>
    public SqlTarget Target(int commandTimeoutSeconds) =>
        new(BuildConnectionString(), commandTimeoutSeconds > 0 ? commandTimeoutSeconds : SqlTarget.DefaultCommandTimeoutSeconds)
        {
            LockTimeoutMilliseconds = SqlTarget.MonitoringLockTimeoutMilliseconds,
        };

    /// <summary>The name and server, never the password.</summary>
    public override string ToString() => $"{DisplayName} ({Summary})";

    public CollectorServer Clone() => (CollectorServer)MemberwiseClone();
}

/// <summary>
/// Everything the collector service is set up with (#172): its servers, its chat channels, and how
/// it samples and keeps history. Saved by <see cref="CollectorSettingsFile"/>.
/// </summary>
public sealed class CollectorSettings
{
    public const int DefaultSampleSeconds = 15;

    /// <summary>The file's format, for a later SqlVitals to read an older file.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Seconds between live samples of each server: 5 to 300.</summary>
    public int SampleSeconds { get; set; } = DefaultSampleSeconds;

    public int CommandTimeoutSeconds { get; set; } = SqlTarget.DefaultCommandTimeoutSeconds;

    /// <summary>Minutes between detail snapshots; one of <see cref="HistorySettings.IntervalChoicesMinutes"/>.</summary>
    public int HistoryIntervalMinutes { get; set; } = HistorySettings.DefaultIntervalMinutes;

    /// <summary>Days of history kept; one of <see cref="HistorySettings.RetentionChoicesDays"/>.</summary>
    public int HistoryRetentionDays { get; set; } = HistorySettings.DefaultRetentionDays;

    /// <summary>Samples in a row that start and end an alert; one of <see cref="AlertSettings.SampleChoices"/>.</summary>
    public int AlertSamples { get; set; } = AlertSettings.DefaultSamples;

    /// <summary>Health thresholds for every server without its own.</summary>
    public HealthThresholds HealthThresholds { get; set; } = HealthThresholds.Default;

    /// <summary>
    /// Minutes between share copies of the history (<see cref="HistoryShareCopy"/>), for the app on
    /// other PCs to read (#172): 5 to 1440; 0, the default, writes none.
    /// </summary>
    public int ShareCopyMinutes { get; set; }

    /// <summary>The folder the share copy is written to; empty for the data folder's share\.</summary>
    public string ShareCopyFolder { get; set; } = string.Empty;

    /// <summary>Where the share copy goes, for a data folder; a relative folder is in it, not where the service runs.</summary>
    public string ShareCopyPath(string dataDirectory) =>
        Path.Combine(string.IsNullOrWhiteSpace(ShareCopyFolder) ? Path.Combine(dataDirectory, "share")
                     : Path.IsPathFullyQualified(ShareCopyFolder.Trim()) ? ShareCopyFolder.Trim()
                     : Path.GetFullPath(Path.Combine(dataDirectory, ShareCopyFolder.Trim())),
                     HistoryShareCopy.FileName);

    public static int NormalizeShareCopyMinutes(int minutes) => minutes is >= 5 and <= 1440 ? minutes : 0;

    public List<CollectorServer> Servers { get; set; } = new();

    /// <summary>The Slack and Teams channels, with the same choices as the app's (#120, #121, #170).</summary>
    public List<ChatChannel> ChatChannels { get; set; } = new();

    /// <summary>
    /// Channels whose webhook or ping URL can't be decrypted on this PC, as the file has them:
    /// not posted to, saved back unchanged, and found by <see cref="FindChannel"/> so they can be
    /// removed and added again.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public List<ChatChannel> UnreadableChannels { get; } = new();

    public HealthThresholds ThresholdsFor(CollectorServer server) => server.HealthThresholds ?? HealthThresholds;

    public static int NormalizeSampleSeconds(int seconds) => seconds is >= 5 and <= 300 ? seconds : DefaultSampleSeconds;

    /// <summary>Brings every value into range, as the app does with its settings file.</summary>
    public void Normalize()
    {
        SampleSeconds          = NormalizeSampleSeconds(SampleSeconds);
        CommandTimeoutSeconds  = CommandTimeoutSeconds > 0 ? CommandTimeoutSeconds : SqlTarget.DefaultCommandTimeoutSeconds;
        HistoryIntervalMinutes = HistorySettings.NormalizeInterval(HistoryIntervalMinutes);
        HistoryRetentionDays   = HistorySettings.NormalizeRetention(HistoryRetentionDays);
        AlertSamples           = AlertSettings.NormalizeSamples(AlertSamples);
        ShareCopyMinutes       = NormalizeShareCopyMinutes(ShareCopyMinutes);
        ShareCopyFolder      ??= string.Empty;
        HealthThresholds       = HealthThresholds.Normalize(HealthThresholds);
        Servers                = (Servers ?? []).Where(s => s is not null).ToList();
        ChatChannels           = (ChatChannels ?? []).Where(c => c is not null).ToList();

        foreach (var server in Servers)
        {
            // Typed in by hand without one: the same id at every read, so its history stays one.
            if (!server.HasFileId)
                server.Id = StableId($"server|{server.Server?.Trim()}|{server.Database?.Trim()}");
            if (server.HealthThresholds is not null)
                server.HealthThresholds = HealthThresholds.Normalize(server.HealthThresholds);
            server.Name                 ??= string.Empty;
            server.Server               ??= string.Empty;
            server.Database             ??= string.Empty;
            server.UserName             ??= string.Empty;
            server.Password             ??= string.Empty;
            server.AdditionalParameters ??= string.Empty;
        }

        var known = Servers.Select(s => s.Id).ToHashSet();
        foreach (var channel in ChatChannels)
        {
            if (channel.Id == Guid.Empty)
                channel.Id = StableId($"channel|{channel.Name?.Trim()}");
            channel.Name             ??= string.Empty;
            channel.WebhookUrl       ??= string.Empty;
            channel.HeartbeatPingUrl ??= string.Empty;
            channel.ConnectionIds    = (channel.ConnectionIds ?? []).Where(known.Contains).Distinct().ToList();
        }
    }

    /// <summary>A server by its id, its name or its server name, ignoring case; null for none.</summary>
    public CollectorServer? FindServer(string nameOrId) =>
        Guid.TryParse(nameOrId, out var id)
            ? Servers.FirstOrDefault(s => s.Id == id)
            : Servers.FirstOrDefault(s => string.Equals(s.DisplayName, nameOrId.Trim(), StringComparison.OrdinalIgnoreCase))
              ?? Servers.FirstOrDefault(s => string.Equals(s.Server.Trim(), nameOrId.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>A chat channel by its id or its name, ignoring case, readable or not; null for none.</summary>
    public ChatChannel? FindChannel(string nameOrId) =>
        Guid.TryParse(nameOrId, out var id)
            ? ChatChannels.Concat(UnreadableChannels).FirstOrDefault(c => c.Id == id)
            : ChatChannels.Concat(UnreadableChannels).FirstOrDefault(c => string.Equals(c.Name.Trim(), nameOrId.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Removes a channel, readable or not.</summary>
    public bool RemoveChannel(ChatChannel channel) => ChatChannels.Remove(channel) | UnreadableChannels.Remove(channel);

    // An id from the text: the same every time.
    private static Guid StableId(string text) =>
        new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text.ToUpperInvariant())).AsSpan(0, 16));
}
