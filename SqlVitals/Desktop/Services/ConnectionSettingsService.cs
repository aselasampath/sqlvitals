using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using SqlVitals.Engine.Activity;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Backups;
using SqlVitals.Engine.ChatNotifications;
using SqlVitals.Engine.FileIo;
using SqlVitals.Engine.History;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.QueryStore;
using SqlVitals.Engine.Regressions;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Persists the user's saved SQL Server connections to a per-user encrypted file.
/// The connection details are protected with Windows DPAPI (current-user scope)
/// so they are unreadable by other OS accounts.
/// </summary>
public class ConnectionSettingsService : ISectionFolds
{
    private static readonly string DefaultSettingsDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SqlVitals");

    private readonly string SettingsDir;
    private readonly string SettingsFile;

    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // Passwords for connections without "Remember password", held for this process only so
    // switching back to such a connection doesn't ask for the password again.
    private readonly Dictionary<Guid, string> _sessionPasswords = new();

    /// <param name="settingsDir">The folder settings.dat is in; %AppData%\SqlVitals when null. Tests use a temp folder.</param>
    public ConnectionSettingsService(string? settingsDir = null)
    {
        SettingsDir  = settingsDir ?? DefaultSettingsDir;
        SettingsFile = Path.Combine(SettingsDir, "settings.dat");
    }

    /// <summary>
    /// Raised after a save that changed anything the activity log records (#115), with a row per
    /// change. Never carries a secret (see <see cref="SettingsDiff"/>).
    /// </summary>
    public event Action<IReadOnlyList<ActivityEntry>>? Changed;

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
    /// <param name="notifications">
    /// Whether its alerts raise Windows notifications, saved in the same write (#131); null leaves it as it is.
    /// </param>
    public void Upsert(ConnectionSettings settings, bool? notifications = null) => Update(ActivitySources.Settings, store =>
    {
        var index = store.Connections.FindIndex(c => c.Id == settings.Id);
        if (index >= 0)
            store.Connections[index] = settings.Clone();
        else
            store.Connections.Add(settings.Clone());

        store.CommandTimeoutSeconds = settings.CommandTimeoutSeconds;
        if (notifications is { } notify)
        {
            if (notify)
                store.NotificationsMuted.Remove(settings.Id);
            else
                store.NotificationsMuted.Add(settings.Id);
        }

        if (settings.SavePassword || string.IsNullOrEmpty(settings.Password))
            _sessionPasswords.Remove(settings.Id);
        else
            _sessionPasswords[settings.Id] = settings.Password;
    });

    /// <summary>Removes a saved connection. Clears the active connection when it was the one removed.</summary>
    public void Remove(Guid id) => Update(ActivitySources.Settings, store =>
    {
        store.Connections.RemoveAll(c => c.Id == id);
        store.NotificationsMuted.Remove(id);
        foreach (var channel in store.ChatChannels)
            channel.ConnectionIds.Remove(id);
        if (store.ActiveConnectionId == id)
            store.ActiveConnectionId = null;

        _sessionPasswords.Remove(id);
    });

    /// <summary>
    /// Saves whether monitoring starts by itself when SqlVitals starts or connects (#115). Off,
    /// nothing is collected until the user presses Start.
    /// </summary>
    public void SetStartMonitoringAutomatically(bool start) =>
        Update(ActivitySources.Settings, store => store.StartMonitoringAutomatically = start);

    /// <summary>
    /// Saves how long a query may run, for every connection. In Settings → Monitoring &amp; history
    /// since #131; before, it was saved with the connection form.
    /// </summary>
    public void SetCommandTimeout(int seconds) =>
        Update(ActivitySources.Settings, store => store.CommandTimeoutSeconds = seconds);

    /// <summary>Saves how often history detail snapshots are taken and how long history is kept.</summary>
    public void SetHistorySettings(int intervalMinutes, int retentionDays) => Update(ActivitySources.Settings, store =>
    {
        store.HistoryIntervalMinutes = HistorySettings.NormalizeInterval(intervalMinutes);
        store.HistoryRetentionDays   = HistorySettings.NormalizeRetention(retentionDays);
    });

    /// <summary>Saves when the Query Regressions page flags a query (see <see cref="RegressionCriteria"/>).</summary>
    public void SetRegressionSettings(RegressionCriteria criteria) => Update(ActivitySources.Page("Query Regressions"), store =>
    {
        store.RegressionThresholdPct  = RegressionCriteria.NormalizeThreshold(criteria.ThresholdPct);
        store.RegressionMinExecutions = RegressionCriteria.NormalizeMinExecutions(criteria.MinExecutions);
    });

    /// <summary>Saves the RPO the Backups page checks every database against (see <see cref="BackupRpo"/>).</summary>
    public void SetBackupRpo(BackupRpo rpo) =>
        Update(ActivitySources.Page("Backups"), store => store.BackupRpo = rpo);

    /// <summary>
    /// Saves when the File I/O page highlights a file (see <see cref="FileLatencyThresholds"/>)
    /// and how often it reads the files.
    /// </summary>
    public void SetFileIoSettings(FileLatencyThresholds thresholds, int intervalSeconds) => Update(ActivitySources.Page("File I/O"), store =>
    {
        store.FileLatencyThresholds = new FileLatencyThresholds(FileLatencyThresholds.NormalizeData(thresholds.DataMs),
                                                                FileLatencyThresholds.NormalizeLog(thresholds.LogMs));
        store.FileIoIntervalSeconds = ConnectionStore.NormalizeFileIoInterval(intervalSeconds);
    });

    /// <summary>Saves the health-dot thresholds used by every connection without its own.</summary>
    public void SetHealthThresholds(HealthThresholds thresholds) =>
        Update(ActivitySources.Settings, store => store.HealthThresholds = HealthThresholds.Normalize(thresholds));

    /// <summary>Saves how many samples in a row start and end an alert (see <see cref="AlertSettings"/>).</summary>
    public void SetAlertSettings(int samples) =>
        Update(ActivitySources.Settings, store => store.AlertSamples = AlertSettings.NormalizeSamples(samples));

    /// <summary>
    /// Turns Windows notifications for a connection's alerts off or on. Saved on its own, without
    /// the connection form, so a server that is down can still be muted.
    /// </summary>
    /// <param name="source">Where it was changed, for the activity log: Settings or the tray menu.</param>
    public void SetNotificationsMuted(Guid id, bool muted, string source = ActivitySources.Settings) => Update(source, store =>
    {
        if (muted && store.Connections.Any(c => c.Id == id))
            store.NotificationsMuted.Add(id);
        else
            store.NotificationsMuted.Remove(id);
    });

    /// <summary>Saves whether minimizing or closing the window leaves SqlVitals running in the tray.</summary>
    public void SetKeepRunningInTray(bool keepRunning) =>
        Update(ActivitySources.Settings, store => store.KeepRunningInTray = keepRunning);

    /// <summary>Remembers that the "still running in the tray" notification has been shown once.</summary>
    public void SetTrayHintShown() =>
        Update(ActivitySources.Settings, store => store.TrayHintShown = true);

    /// <summary>Saves the Processes page's auto-refresh: its interval, and whether it starts on its own.</summary>
    public void SetProcessesRefresh(int seconds, bool autoRefresh) => Update(ActivitySources.Page("Processes"), store =>
    {
        store.ProcessesRefreshSeconds = ConnectionStore.NormalizeProcessesRefresh(seconds);
        store.ProcessesAutoRefresh    = autoRefresh;
    });

    /// <summary>Saves how the Processes page shows its sessions: the map or the grid, and whether the grid is grouped by login (#116).</summary>
    public void SetProcessesView(ProcessesView view, bool groupByLogin) => Update(ActivitySources.Page("Processes"), store =>
    {
        store.ProcessesView         = view;
        store.ProcessesGroupByLogin = groupByLogin;
    });

    /// <summary>Saves which metrics the Query Store page charts over time.</summary>
    public void SetQueryStoreChartMetrics(IReadOnlyList<QueryStoreMetric> metrics) => Update(ActivitySources.Page("Query Store"), store =>
        store.QueryStoreChartMetrics = QueryStoreMetrics.ParseChartMetrics(metrics.Select(m => m.ToString())));

    /// <summary>
    /// Saves the page on screen and the server it is inside (#141), to open again at start-up. Only
    /// written when it changed. Not in the activity log (see <see cref="SettingsActivity"/>), so the
    /// source is never shown.
    /// </summary>
    public void SetLastPage(string tag, Guid? serverId)
    {
        var store = Load();
        if (store.LastPage == tag && store.LastServerId == serverId)
            return;
        Update(ActivitySources.Page("Sidebar"), s =>
        {
            s.LastPage     = tag;
            s.LastServerId = serverId;
        });
    }

    /// <summary>
    /// Saves when a server's Overview was opened (#143), for its "Since you last looked". Servers
    /// no longer saved are forgotten. Not in the activity log (see <see cref="SettingsActivity"/>):
    /// it changes every time an Overview opens.
    /// </summary>
    public void SetOverviewOpened(Guid connectionId, DateTime utc) => Update(ActivitySources.Page("Overview"), s =>
    {
        s.OverviewOpenedUtc[connectionId] = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        foreach (var gone in s.OverviewOpenedUtc.Keys.Where(id => s.Connections.All(c => c.Id != id)).ToList())
            s.OverviewOpenedUtc.Remove(gone);
    });

    /// <summary>The sections of a page folded to their header (#146).</summary>
    public IReadOnlySet<string> Folded(string page)
    {
        var prefix = page + "/";
        return Load().FoldedSections.Where(k => k.StartsWith(prefix, StringComparison.Ordinal))
                                    .Select(k => k[prefix.Length..])
                                    .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// Saves a page section folded or unfolded (#146). Only written when it changed. A layout
    /// preference, so not in the activity log (see <see cref="SettingsActivity"/>).
    /// </summary>
    public void SetFolded(string page, string section, bool folded)
    {
        var key = ConnectionStore.FoldKey(page, section);
        if (Load().FoldedSections.Contains(key) == folded)
            return;
        Update(ActivitySources.Page(page), s =>
        {
            if (folded) s.FoldedSections.Add(key);
            else        s.FoldedSections.Remove(key);
        });
    }

    /// <summary>
    /// Saves the sidebar's state (#142): pinned, collapsed or hidden. Only written when it changed.
    /// A layout preference, so not in the activity log (see <see cref="SettingsActivity"/>).
    /// </summary>
    public void SetSidebarMode(SidebarMode mode)
    {
        if (Load().SidebarMode == mode)
            return;
        Update(ActivitySources.Page("Sidebar"), s => s.SidebarMode = mode);
    }

    /// <summary>
    /// Adds a Slack or Teams channel (#120), or replaces the saved one with the same
    /// <see cref="ChatChannel.Id"/>. The caller checks the URL first (see <see cref="WebhookUrl.Check"/>).
    /// </summary>
    public void SaveChatChannel(ChatChannel channel) => Update(ActivitySources.Settings, store =>
    {
        var index = store.ChatChannels.FindIndex(c => c.Id == channel.Id);
        var copy  = channel.Clone();
        copy.Name       = copy.Name.Trim();
        copy.WebhookUrl = copy.WebhookUrl.Trim();
        if (index >= 0)
            store.ChatChannels[index] = copy;
        else
            store.ChatChannels.Add(copy);
    });

    /// <summary>Removes a chat channel.</summary>
    public void RemoveChatChannel(Guid id) =>
        Update(ActivitySources.Settings, store => store.ChatChannels.RemoveAll(c => c.Id == id));

    /// <summary>Marks a saved connection as the one the dashboard uses; null leaves none active.</summary>
    /// <param name="source">Where the user switched, for the activity log: the selector, or Home (#130).</param>
    public void SetActive(Guid? id, string source = ActivitySources.ConnectionSelector) => Update(source, store =>
        store.ActiveConnectionId = id is { } value && store.Connections.Any(c => c.Id == value) ? value : null);

    // Every setter goes through here: load, change, save, and tell the activity log what changed,
    // compared on the whole store so no setting can be left out.
    private void Update(string source, Action<ConnectionStore> change)
    {
        var store  = Load();
        var before = SettingsActivity.Snapshot(store);
        change(store);
        Save(store);

        var changes = SettingsDiff.Between(before, SettingsActivity.Snapshot(store), source, DateTime.UtcNow,
                                           SettingsActivity.Describe(store));
        if (changes.Count > 0)
            Changed?.Invoke(changes);
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
    // Every setter loads, changes one thing and saves the whole store. So a read that fails must
    // never hand back an empty store that the next save writes over the file: that would delete
    // every saved connection. A file that can't be read for now (another program has it open) is
    // left alone until it can; one that can't be read at all (damaged, or encrypted for another
    // Windows user) is copied aside first, then replaced.

    // One copy of an unreadable file per run: every Load until the next save reads the same file.
    private bool _unreadableCopied;

    private ConnectionStore ReadFile()
    {
        if (!File.Exists(SettingsFile))
            return new ConnectionStore();

        string text;
        try
        {
            text = ReadAllTextWithRetry(SettingsFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("The settings file couldn't be read, so it won't be saved over until it can", ex);
            return new ConnectionStore { ReadFailed = true };
        }

        try
        {
            var raw = JsonSerializer.Deserialize<RawSettings>(text);
            if (raw is null) return Unreadable(new JsonException("The settings file is empty."));

            var store = new ConnectionStore
            {
                CommandTimeoutSeconds  = raw.CommandTimeoutSeconds > 0 ? raw.CommandTimeoutSeconds : 30,
                ActiveConnectionId     = raw.ActiveConnectionId,
                HistoryIntervalMinutes = HistorySettings.NormalizeInterval(raw.HistoryIntervalMinutes),
                HistoryRetentionDays   = HistorySettings.NormalizeRetention(raw.HistoryRetentionDays),
                RegressionThresholdPct  = RegressionCriteria.NormalizeThreshold(raw.RegressionThresholdPct),
                RegressionMinExecutions = RegressionCriteria.NormalizeMinExecutions(raw.RegressionMinExecutions),
                HealthThresholds        = HealthThresholds.Normalize(raw.HealthThresholds),
                BackupRpo               = new BackupRpo(BackupRpo.NormalizeFull(raw.BackupFullMaxAgeMinutes),
                                                        BackupRpo.NormalizeLog(raw.BackupLogMaxAgeMinutes)),
                FileLatencyThresholds   = new FileLatencyThresholds(FileLatencyThresholds.NormalizeData(raw.FileLatencyDataMs),
                                                                    FileLatencyThresholds.NormalizeLog(raw.FileLatencyLogMs)),
                FileIoIntervalSeconds   = ConnectionStore.NormalizeFileIoInterval(raw.FileIoIntervalSeconds),
                AlertSamples            = AlertSettings.NormalizeSamples(raw.AlertSamples),
                KeepRunningInTray       = raw.KeepRunningInTray,
                TrayHintShown           = raw.TrayHintShown,
                ProcessesRefreshSeconds = ConnectionStore.NormalizeProcessesRefresh(raw.ProcessesRefreshSeconds),
                ProcessesAutoRefresh    = raw.ProcessesAutoRefresh,
                ProcessesView           = ConnectionStore.ParseProcessesView(raw.ProcessesView),
                ProcessesGroupByLogin   = raw.ProcessesGroupByLogin,
                QueryStoreChartMetrics  = QueryStoreMetrics.ParseChartMetrics(raw.QueryStoreChartMetrics),
                LastPage                = LastPageFrom(raw.LastPage),
                LastServerId            = raw.LastServerId,
                SidebarMode             = ConnectionStore.ParseSidebarMode(raw.SidebarMode),
                OverviewOpenedUtc       = raw.OverviewOpenedUtc?.ToDictionary(p => p.Key, p => DateTime.SpecifyKind(p.Value, DateTimeKind.Utc)) ?? [],
                FoldedSections          = raw.FoldedSections?.Where(k => !string.IsNullOrWhiteSpace(k)).ToHashSet(StringComparer.Ordinal) ?? [],
            };

            var migrated = false;
            if (!string.IsNullOrWhiteSpace(raw.EncryptedConnections))
            {
                // Encrypted for another Windows user or PC (a copied or restored profile): the
                // other settings are still good, so only the connections are lost, and the file
                // is copied aside first.
                try
                {
                    store.Connections = JsonSerializer.Deserialize<List<ConnectionSettings>>(Decrypt(raw.EncryptedConnections))
                                        ?? new List<ConnectionSettings>();
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
                {
                    if (!CopyUnreadable(ex))
                        return new ConnectionStore { ReadFailed = true };
                }
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
                    // These formats predate the box, when every connection was monitored in the background.
                    legacy.MonitorInBackground = true;
                    store.Connections.Add(legacy);
                    store.ActiveConnectionId = legacy.Id;
                }

                migrated = true;
            }

            // The chat channels (#120) are their own blob, so one that can't be decrypted (the file
            // was copied to another PC or Windows user, or the user's password was reset by an
            // admin) loses only the channels. They're dropped and the app says so (see
            // ConnectionStore.ChatChannelsDropped); the exception isn't logged as it is, in case
            // it quotes part of the blob.
            if (!string.IsNullOrWhiteSpace(raw.EncryptedChatChannels))
            {
                try
                {
                    store.ChatChannels = JsonSerializer.Deserialize<List<ChatChannel>>(Decrypt(raw.EncryptedChatChannels))
                                         ?? new List<ChatChannel>();
                }
                catch (Exception ex) when (ex is CryptographicException or FormatException or JsonException)
                {
                    AppLog.Info($"The chat channels in the settings file couldn't be read ({ex.GetType().Name}), " +
                                "so they were dropped. Add them again in Settings → Alerts & chat.");
                    store.ChatChannelsDropped = true;
                }
            }

            // Missing from files written before #115: those installations were monitoring their
            // connections, and keep doing so. Saved explicitly from then on.
            store.StartMonitoringAutomatically =
                MonitoringPlan.StartAutomatically(raw.StartMonitoringAutomatically, store.Connections.Count > 0);

            // Write the new format straight away: the Id is generated here, so leaving the old
            // file in place would hand out a different Id on every load.
            if (migrated)
                try { Save(store); } catch { /* retried on the next load */ }

            // Only saved connections can be muted; anything else is dropped on the next save.
            var known = store.Connections.Select(c => c.Id).ToHashSet();
            store.NotificationsMuted = (raw.NotificationsMuted ?? []).Where(known.Contains).ToHashSet();

            // Likewise a chat channel's chosen connections (#121).
            foreach (var channel in store.ChatChannels)
                channel.ConnectionIds = (channel.ConnectionIds ?? []).Where(known.Contains).Distinct().ToList();

            // Guard against hand-edited files; persist so the Ids stay stable.
            var missingIds = store.Connections.Where(c => c.Id == Guid.Empty).ToList();
            foreach (var conn in missingIds)
                conn.Id = Guid.NewGuid();
            if (missingIds.Count > 0)
                try { Save(store); } catch { /* retried on the next load */ }

            return store;
        }
        catch (Exception ex)
        {
            return Unreadable(ex);
        }
    }

    // Damaged beyond reading: start afresh, once a copy is kept.
    private ConnectionStore Unreadable(Exception ex) =>
        CopyUnreadable(ex) ? new ConnectionStore() : new ConnectionStore { ReadFailed = true };

    /// <summary>
    /// Copies the settings file to settings.unreadable-&lt;time&gt;.dat, so the next save can't lose
    /// what was in it. False when the copy couldn't be made: then nothing may be saved over it.
    /// </summary>
    private bool CopyUnreadable(Exception ex)
    {
        // Every Load reads the file again until the next save replaces it: say so once.
        if (_unreadableCopied)
            return true;
        AppLog.Error("The settings file couldn't be read", ex);

        try
        {
            var copy = Path.Combine(SettingsDir, $"settings.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}.dat");
            File.Copy(SettingsFile, copy, overwrite: false);
            _unreadableCopied = true;
            return true;
        }
        catch (Exception copyEx) when (copyEx is IOException or UnauthorizedAccessException)
        {
            AppLog.Error("Could not keep a copy of the unreadable settings file", copyEx);
            return false;
        }
    }

    // A page still in the sidebar. A server's summary, saved before 0.59.0, is its Overview now (#143).
    private static string? LastPageFrom(string? saved)
    {
        var tag = saved == "ServerDetail" ? "Overview" : saved;
        return tag is not null && NavMenu.AllPages.Any(p => p.Tag == tag) ? tag : null;
    }

    // A sync or antivirus tool can hold the file for a moment.
    private static string ReadAllTextWithRetry(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(100);
            }
        }
    }

    private void Save(ConnectionStore store)
    {
        if (store.ReadFailed)
            throw new IOException("SqlVitals couldn't read its settings file just now, so it didn't save over it. " +
                                  "Another program (a sync or antivirus tool) may have it open. Try again in a moment.");

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
            EncryptedChatChannels  = store.ChatChannels.Count > 0 ? Encrypt(JsonSerializer.Serialize(store.ChatChannels)) : null,
            ActiveConnectionId     = store.ActiveConnectionId,
            StartMonitoringAutomatically = store.StartMonitoringAutomatically,
            CommandTimeoutSeconds  = store.CommandTimeoutSeconds,
            HistoryIntervalMinutes = store.HistoryIntervalMinutes,
            HistoryRetentionDays   = store.HistoryRetentionDays,
            RegressionThresholdPct  = store.RegressionThresholdPct,
            RegressionMinExecutions = store.RegressionMinExecutions,
            HealthThresholds        = store.HealthThresholds,
            BackupFullMaxAgeMinutes = (int)store.BackupRpo.FullMaxAge.TotalMinutes,
            BackupLogMaxAgeMinutes  = (int)store.BackupRpo.LogMaxAge.TotalMinutes,
            FileLatencyDataMs       = store.FileLatencyThresholds.DataMs,
            FileLatencyLogMs        = store.FileLatencyThresholds.LogMs,
            FileIoIntervalSeconds   = store.FileIoIntervalSeconds,
            AlertSamples            = store.AlertSamples,
            NotificationsMuted      = store.NotificationsMuted.Count > 0 ? store.NotificationsMuted.ToList() : null,
            KeepRunningInTray       = store.KeepRunningInTray,
            TrayHintShown           = store.TrayHintShown,
            ProcessesRefreshSeconds = store.ProcessesRefreshSeconds,
            ProcessesAutoRefresh    = store.ProcessesAutoRefresh,
            ProcessesView           = store.ProcessesView.ToString(),
            ProcessesGroupByLogin   = store.ProcessesGroupByLogin,
            QueryStoreChartMetrics  = store.QueryStoreChartMetrics.Select(m => m.ToString()).ToList(),
            LastPage                = store.LastPage,
            LastServerId            = store.LastServerId,
            SidebarMode             = store.SidebarMode.ToString(),
            OverviewOpenedUtc       = store.OverviewOpenedUtc.Count > 0 ? new Dictionary<Guid, DateTime>(store.OverviewOpenedUtc) : null,
            FoldedSections          = store.FoldedSections.Count > 0 ? store.FoldedSections.Order(StringComparer.Ordinal).ToList() : null,
        };

        // Written beside it and swapped in, so a crash or full disk mid-write can't leave half a file.
        var temp = SettingsFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(raw, FileJsonOptions));
        File.Move(temp, SettingsFile, overwrite: true);
        _unreadableCopied = false;
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

        // The Slack and Teams channels (#120): their webhook URLs are secrets, so encrypted too.
        public string? EncryptedChatChannels     { get; set; }

        public Guid?   ActiveConnectionId        { get; set; }
        public int     CommandTimeoutSeconds     { get; set; } = 30;

        // Null in files written before #115, so missing can be told apart from off (see ReadFile).
        public bool?   StartMonitoringAutomatically { get; set; }

        // Missing from files written before these settings existed: the defaults apply.
        public int     HistoryIntervalMinutes    { get; set; } = HistorySettings.DefaultIntervalMinutes;
        public int     HistoryRetentionDays      { get; set; } = HistorySettings.DefaultRetentionDays;
        public double  RegressionThresholdPct    { get; set; } = RegressionCriteria.DefaultThresholdPct;
        public long    RegressionMinExecutions   { get; set; } = RegressionCriteria.DefaultMinExecutions;
        public HealthThresholds? HealthThresholds { get; set; }
        public int     BackupFullMaxAgeMinutes   { get; set; } = (int)BackupRpo.DefaultFullMaxAge.TotalMinutes;
        public int     BackupLogMaxAgeMinutes    { get; set; } = (int)BackupRpo.DefaultLogMaxAge.TotalMinutes;
        public double  FileLatencyDataMs         { get; set; } = FileLatencyThresholds.DefaultDataMs;
        public double  FileLatencyLogMs          { get; set; } = FileLatencyThresholds.DefaultLogMs;
        public int     FileIoIntervalSeconds     { get; set; } = ConnectionStore.DefaultFileIoIntervalSeconds;
        public int     AlertSamples             { get; set; } = AlertSettings.DefaultSamples;
        public List<Guid>? NotificationsMuted    { get; set; }
        public bool    KeepRunningInTray         { get; set; } = true;
        public bool    TrayHintShown             { get; set; }
        public int     ProcessesRefreshSeconds   { get; set; } = ConnectionStore.DefaultProcessesRefreshSeconds;
        public bool    ProcessesAutoRefresh      { get; set; }
        public string? ProcessesView             { get; set; }
        public bool    ProcessesGroupByLogin     { get; set; } = true;
        public List<string>? QueryStoreChartMetrics { get; set; }
        public string? LastPage                  { get; set; }
        public Guid?   LastServerId              { get; set; }
        public string? SidebarMode               { get; set; }
        public Dictionary<Guid, DateTime>? OverviewOpenedUtc { get; set; }
        public List<string>? FoldedSections      { get; set; }

        // Legacy single-connection formats: read on load, never written.
        public string? EncryptedConnection       { get; set; }
        public string? EncryptedConnectionString { get; set; }
    }
}

/// <summary>Decrypted, in-memory view of everything in the settings file.</summary>
public class ConnectionStore
{
    /// <summary>The file was there but couldn't be read, so this store is empty and must not be saved over it.</summary>
    internal bool ReadFailed { get; init; }

    public List<ConnectionSettings> Connections           { get; set; } = new();
    public Guid?                    ActiveConnectionId    { get; set; }

    /// <summary>The Slack and Teams channels alerts are posted to (#108).</summary>
    public List<ChatChannel>        ChatChannels          { get; set; } = new();

    /// <summary>
    /// The file had chat channels that couldn't be decrypted, so they were dropped (#120). Only
    /// until the next save, which writes the file without them.
    /// </summary>
    public bool ChatChannelsDropped { get; internal set; }
    public int                      CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Monitor the active connection, and those with Monitor in background, as soon as SqlVitals
    /// starts or connects (#115). Off for a new installation: nothing is collected until the user
    /// presses Start.
    /// </summary>
    public bool StartMonitoringAutomatically { get; set; }

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

    /// <summary>How old a full and a log backup may get before the Backups page warns (#40).</summary>
    public BackupRpo BackupRpo { get; set; } = BackupRpo.Default;

    /// <summary>The average read or write latency above which the File I/O page highlights a file (#41).</summary>
    public FileLatencyThresholds FileLatencyThresholds { get; set; } = FileLatencyThresholds.Default;

    public const int DefaultFileIoIntervalSeconds = 10;

    /// <summary>The File I/O page's choices of seconds between readings.</summary>
    public static readonly int[] FileIoIntervalChoices = [5, 10, 15, 30, 60];

    /// <summary>Seconds between the File I/O page's readings; one of <see cref="FileIoIntervalChoices"/>.</summary>
    public int FileIoIntervalSeconds { get; set; } = DefaultFileIoIntervalSeconds;

    public static int NormalizeFileIoInterval(int seconds) =>
        FileIoIntervalChoices.Contains(seconds) ? seconds : DefaultFileIoIntervalSeconds;

    /// <summary>
    /// Samples in a row an indicator must stay past a threshold for an alert to start, and back
    /// to normal for it to end; one of <see cref="AlertSettings.SampleChoices"/>.
    /// </summary>
    public int AlertSamples { get; set; } = AlertSettings.DefaultSamples;

    /// <summary>Saved connections whose alerts raise no Windows notification (#35).</summary>
    public HashSet<Guid> NotificationsMuted { get; set; } = new();

    /// <summary>Whether a connection's alerts raise a Windows notification.</summary>
    public bool NotifiesFor(Guid connectionId) => !NotificationsMuted.Contains(connectionId);

    /// <summary>
    /// Minimizing or closing the window hides it to the notification area, and monitoring
    /// carries on; off, they minimize to the taskbar and exit as usual.
    /// </summary>
    public bool KeepRunningInTray { get; set; } = true;

    /// <summary>The one-time "still running in the tray" notification has been shown.</summary>
    public bool TrayHintShown { get; set; }

    public const int DefaultProcessesRefreshSeconds = 10;

    /// <summary>Seconds between Processes page refreshes: 5 to 120, in steps of 5.</summary>
    public int ProcessesRefreshSeconds { get; set; } = DefaultProcessesRefreshSeconds;

    /// <summary>The Processes page starts refreshing on its own when opened (#37).</summary>
    public bool ProcessesAutoRefresh { get; set; }

    /// <summary>The Processes page shows the map or the grid (#116): the map unless the user picks the grid.</summary>
    public ProcessesView ProcessesView { get; set; } = ProcessesView.Map;

    /// <summary>The Processes grid is grouped by login (#116); off, it is one flat list.</summary>
    public bool ProcessesGroupByLogin { get; set; } = true;

    /// <summary>A saved Processes view; the map for anything missing or unknown.</summary>
    public static ProcessesView ParseProcessesView(string? value) =>
        Enum.TryParse<ProcessesView>(value, ignoreCase: true, out var view) && Enum.IsDefined(view) ? view : ProcessesView.Map;

    /// <summary>The Query Store page's Over Time charts, in order (#111).</summary>
    public IReadOnlyList<QueryStoreMetric> QueryStoreChartMetrics { get; set; } = QueryStoreMetrics.DefaultChartMetrics;

    /// <summary>The page on screen when SqlVitals last closed (#141), a tag in <see cref="NavMenu"/>; null for none.</summary>
    public string? LastPage { get; set; }

    /// <summary>The server <see cref="LastPage"/> was inside; null for a page about every server.</summary>
    public Guid? LastServerId { get; set; }

    /// <summary>The sidebar pinned, collapsed or hidden (#142), as the user last chose it.</summary>
    public SidebarMode SidebarMode { get; set; } = SidebarMode.Pinned;

    /// <summary>When each server's Overview was last opened (#143), UTC, by connection id.</summary>
    public Dictionary<Guid, DateTime> OverviewOpenedUtc { get; set; } = [];

    /// <summary>The page sections folded to their header (#146), as "Page/section" keys.</summary>
    public HashSet<string> FoldedSections { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The key a folded section is saved under: "Backups/verdict".</summary>
    public static string FoldKey(string page, string section) => $"{page}/{section}";

    /// <summary>A saved sidebar state; pinned for anything missing or unknown.</summary>
    public static SidebarMode ParseSidebarMode(string? value) =>
        Enum.TryParse<SidebarMode>(value, ignoreCase: true, out var mode) && Enum.IsDefined(mode) ? mode : SidebarMode.Pinned;

    public static int NormalizeProcessesRefresh(int seconds) =>
        seconds is >= 5 and <= 120 && seconds % 5 == 0 ? seconds : DefaultProcessesRefreshSeconds;

    public ConnectionSettings? Active =>
        ActiveConnectionId is { } id ? Connections.FirstOrDefault(c => c.Id == id) : null;
}

/// <summary>How the Processes page shows its sessions (#116).</summary>
public enum ProcessesView
{
    Map,
    Grid,
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

    /// <summary>
    /// Monitor this connection automatically while another one is active. Off for a new
    /// connection (#115); a saved one keeps its value.
    /// </summary>
    public bool        MonitorInBackground    { get; set; }

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

        // Name SqlVitals' sessions on the server (program_name in sys.dm_exec_sessions, traces,
        // audits, Resource Governor), unless the extra parameters already name them.
        if (csb.ApplicationName == DefaultApplicationName)
            csb.ApplicationName = ApplicationName;

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

    /// <summary>The program name SqlVitals' connections show on the server.</summary>
    public const string ApplicationName = "SqlVitals";

    private static readonly string DefaultApplicationName = new SqlConnectionStringBuilder().ApplicationName;
}
