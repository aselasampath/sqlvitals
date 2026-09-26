using SqlVitals.Engine.Backups;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.Tests.Backups;

public class DatabaseBackupTests
{
    private static readonly DateTime Now = new(2026, 9, 26, 12, 0, 0);

    private static BackupInfo Ago(TimeSpan age, bool copyOnly = false) =>
        new(Now - age - TimeSpan.FromMinutes(5), Now - age, 1_048_576, copyOnly, @"X:\Backup\db.bak");

    private static DatabaseBackupRow Row(
        string name = "Sales",
        string recovery = "FULL",
        string state = "ONLINE",
        bool readOnly = false,
        bool inAg = false,
        bool chainKnown = true,
        bool chainStarted = true,
        BackupInfo? full = null,
        BackupInfo? diff = null,
        BackupInfo? log = null) =>
        new(name, recovery, state, readOnly, new DateTime(2024, 1, 1), inAg, chainKnown, chainStarted, full, diff, log);

    private static DatabaseBackup Check(DatabaseBackupRow row, BackupRpo? rpo = null) =>
        DatabaseBackup.From(row, Now, rpo ?? BackupRpo.Default);

    private static readonly TimeSpan Day = TimeSpan.FromDays(1);

    // ── Full backups ─────────────────────────────────────────────────────────

    [Fact]
    public void Recent_full_and_log_backups_are_ok()
    {
        var db = Check(Row(full: Ago(2 * Day), log: Ago(TimeSpan.FromMinutes(10))));

        Assert.Equal(BackupStatus.Ok, db.Status);
        Assert.Empty(db.Warnings);
        Assert.False(db.NeedsAttention);
        Assert.Equal("2 d", db.FullAgeText);
        Assert.Equal("10 min", db.LogAgeText);
    }

    [Fact]
    public void No_full_backup_is_critical_and_says_log_backups_need_one()
    {
        var db = Check(Row(full: null, log: null));

        Assert.Equal(BackupStatus.NoFullBackup, db.Status);
        Assert.True(db.IsCritical);
        Assert.True(db.FullIsOverdue);
        Assert.Single(db.Warnings);
        Assert.Contains("nothing to restore", db.WarningsText);
        Assert.Contains("Log backups can't start", db.WarningsText);
        Assert.Equal("everything", db.DataAtRiskText);
    }

    [Fact]
    public void A_full_backup_older_than_the_rpo_is_overdue()
    {
        var db = Check(Row(recovery: "SIMPLE", full: Ago(8 * Day)));

        Assert.Equal(BackupStatus.FullOverdue, db.Status);
        Assert.False(db.IsCritical);
        Assert.Contains("more than the 7 d allowed", db.WarningsText);
    }

    [Fact]
    public void A_full_backup_exactly_at_the_limit_is_not_overdue() =>
        Assert.Equal(BackupStatus.Ok, Check(Row(recovery: "SIMPLE", full: Ago(7 * Day))).Status);

    [Fact]
    public void The_rpo_decides_what_is_overdue()
    {
        var row  = Row(recovery: "SIMPLE", full: Ago(TimeSpan.FromHours(30)));
        var tight = new BackupRpo(TimeSpan.FromHours(26), BackupRpo.DefaultLogMaxAge);

        Assert.Equal(BackupStatus.Ok, Check(row).Status);
        Assert.Equal(BackupStatus.FullOverdue, Check(row, tight).Status);
    }

    [Fact]
    public void A_copy_only_full_backup_counts()
    {
        var db = Check(Row(recovery: "SIMPLE", full: Ago(Day, copyOnly: true)));

        Assert.Equal(BackupStatus.Ok, db.Status);
        Assert.True(db.Full!.CopyOnly);
    }

    // ── Log backups ──────────────────────────────────────────────────────────

    [Fact]
    public void A_log_backup_older_than_the_rpo_is_overdue()
    {
        var db = Check(Row(full: Ago(Day), log: Ago(TimeSpan.FromMinutes(95))));

        Assert.Equal(BackupStatus.LogOverdue, db.Status);
        Assert.True(db.LogIsOverdue);
        Assert.False(db.FullIsOverdue);
        Assert.Contains("Last log backup 1 h 35 min ago, more than the 1 h allowed.", db.WarningsText);
    }

    [Fact]
    public void Full_recovery_without_any_log_backup_is_critical()
    {
        var db = Check(Row(full: Ago(Day), log: null));

        Assert.Equal(BackupStatus.NoLogBackup, db.Status);
        Assert.True(db.IsCritical);
        Assert.Contains("never had a log backup", db.WarningsText);
    }

    [Fact]
    public void Bulk_logged_needs_log_backups_too() =>
        Assert.Equal(BackupStatus.LogOverdue,
            Check(Row(recovery: "BULK_LOGGED", full: Ago(Day), log: Ago(3 * TimeSpan.FromHours(1)))).Status);

    [Fact]
    public void A_log_chain_that_has_not_started_is_critical_even_with_old_log_backups()
    {
        // Switched to SIMPLE and back after the last log backup: last_log_backup_lsn is NULL.
        var db = Check(Row(full: Ago(Day), log: Ago(TimeSpan.FromMinutes(5)), chainStarted: false));

        Assert.Equal(BackupStatus.LogChainBroken, db.Status);
        Assert.True(db.IsCritical);
        Assert.Contains("log chain hasn't started", db.WarningsText);
    }

    [Fact]
    public void An_unknown_log_chain_is_not_reported_as_broken()
    {
        // sys.database_recovery_status had no row the login could see.
        var db = Check(Row(full: Ago(Day), log: Ago(TimeSpan.FromMinutes(5)), chainKnown: false, chainStarted: false));

        Assert.Equal(BackupStatus.Ok, db.Status);
    }

    [Fact]
    public void Simple_recovery_has_no_log_check()
    {
        var db = Check(Row(recovery: "SIMPLE", full: Ago(Day), chainStarted: false));

        Assert.Equal(BackupStatus.Ok, db.Status);
        Assert.False(db.UsesLogBackups);
        Assert.Equal("n/a", db.LogAgeText);
    }

    [Theory]
    [InlineData("model", false)]
    [InlineData("MODEL", false)]
    [InlineData("Archive", true)]
    public void Model_and_read_only_databases_need_no_log_backups(string name, bool readOnly)
    {
        var db = Check(Row(name: name, readOnly: readOnly, full: Ago(Day), log: null));

        Assert.Equal(BackupStatus.Ok, db.Status);
        Assert.True(db.UsesLogBackups);
        Assert.False(db.NeedsLogBackups);
    }

    [Fact]
    public void Both_warnings_are_kept_and_the_most_urgent_decides_the_status()
    {
        var db = Check(Row(full: Ago(10 * Day), log: null));

        Assert.Equal(BackupStatus.NoLogBackup, db.Status);
        Assert.Equal([BackupStatus.FullOverdue, BackupStatus.NoLogBackup], db.Warnings.Select(w => w.Kind));
        Assert.True(db.FullIsOverdue);
        Assert.True(db.LogIsOverdue);
    }

    // ── Not online ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("RESTORING", "Restoring")]
    [InlineData("OFFLINE", "Offline")]
    [InlineData("RECOVERY_PENDING", "Recovery pending")]
    public void A_database_that_is_not_online_is_not_checked(string state, string text)
    {
        var db = Check(Row(state: state, full: null));

        Assert.Equal(BackupStatus.NotChecked, db.Status);
        Assert.Empty(db.Warnings);
        Assert.False(db.NeedsAttention);
        Assert.Equal(text, db.StatusText);
        Assert.Contains("isn't checked", db.WarningsText);
    }

    // ── Recovery point ───────────────────────────────────────────────────────

    [Fact]
    public void In_full_recovery_the_last_log_backup_is_the_recovery_point()
    {
        var db = Check(Row(full: Ago(3 * Day), diff: Ago(Day), log: Ago(TimeSpan.FromMinutes(12))));

        Assert.Equal(Now - TimeSpan.FromMinutes(12), db.RecoveryPoint);
        Assert.Equal("12 min", db.DataAtRiskText);
    }

    [Fact]
    public void In_simple_recovery_the_newest_full_or_differential_is_the_recovery_point()
    {
        // A log backup left from before a switch to SIMPLE doesn't count.
        var db = Check(Row(recovery: "SIMPLE", full: Ago(3 * Day), diff: Ago(Day), log: Ago(TimeSpan.FromMinutes(1))));

        Assert.Equal(Now - Day, db.RecoveryPoint);
        Assert.Equal("1 d", db.DataAtRiskText);
    }

    [Fact]
    public void A_differential_without_a_full_is_no_recovery_point()
    {
        var db = Check(Row(recovery: "SIMPLE", full: null, diff: Ago(Day)));

        Assert.Null(db.RecoveryPoint);
        Assert.Null(db.DataAtRisk);
    }

    [Theory]
    [InlineData(20,               "< 1 min")]
    [InlineData(3 * 3600,         "3 h")]
    [InlineData(45 * 60,          "45 min")]
    [InlineData(200 * 60,         "3 h 20 min")]
    [InlineData(52 * 3600,        "2 d 4 h")]
    [InlineData(41 * 86400 + 60,  "41 d")]
    public void FormatAge_shows_the_units_that_matter(int seconds, string expected) =>
        Assert.Equal(expected, DatabaseBackup.FormatAge(TimeSpan.FromSeconds(seconds)));

    // ── The list ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_list_puts_the_most_urgent_first_then_sorts_by_name()
    {
        var list = BackupList.From(
        [
            Row(name: "b_ok",      recovery: "SIMPLE", full: Ago(Day)),
            Row(name: "a_offline", state: "OFFLINE"),
            Row(name: "c_overdue", recovery: "SIMPLE", full: Ago(9 * Day)),
            Row(name: "d_nofull"),
            Row(name: "a_ok",      recovery: "SIMPLE", full: Ago(Day)),
            Row(name: "e_nolog",   full: Ago(Day)),
        ], Now, BackupRpo.Default);

        Assert.Equal(["d_nofull", "e_nolog", "c_overdue", "a_ok", "b_ok", "a_offline"], list.Databases.Select(d => d.Name));
        Assert.Equal(1, list.NoFullCount);
        Assert.Equal(1, list.FullOverdueCount);
        Assert.Equal(1, list.LogProblemCount);
        Assert.Equal(1, list.NotCheckedCount);
        Assert.Equal(3, list.AttentionCount);
        Assert.Null(list.Problem);
        Assert.True(list.Available);
    }

    [Fact]
    public void Databases_in_an_availability_group_get_a_note()
    {
        var list = BackupList.From([Row(inAg: true, full: Ago(Day), log: Ago(TimeSpan.FromMinutes(5))), Row(inAg: true, name: "Other")],
                                   Now, BackupRpo.Default);

        Assert.Equal(BackupList.AvailabilityGroupNote(2), list.Problem);
        Assert.StartsWith("2 databases are in an availability group", list.Problem);
    }

    [Fact]
    public void Unavailable_carries_the_reason()
    {
        var list = BackupList.Unavailable("No msdb", BackupRpo.Default);

        Assert.False(list.Available);
        Assert.Empty(list.Databases);
        Assert.Equal("No msdb", list.Problem);
    }

    // ── History ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("D", false, "Full")]
    [InlineData("D", true,  "Full (copy-only)")]
    [InlineData("I", false, "Differential")]
    [InlineData("L", false, "Log")]
    [InlineData("F", false, "File or filegroup")]
    [InlineData("Q", false, "Differential partial")]
    public void History_names_each_backup_type(string type, bool copyOnly, string expected)
    {
        var entry = new BackupHistoryEntry(type, Now, Now, 10, 10, copyOnly, true, false, "FULL", "sa", null, false);
        Assert.Equal(expected, entry.TypeText);
    }

    [Fact]
    public void History_shows_the_compressed_size_only_when_smaller()
    {
        var compressed = new BackupHistoryEntry("D", Now, Now.AddSeconds(75), 4L << 30, 1L << 30, false, true, false, null, null, null, false);
        var plain      = compressed with { CompressedBytes = compressed.Bytes };

        Assert.Equal("0:01:15", compressed.DurationText);
        Assert.NotEmpty(compressed.CompressedText);
        Assert.Empty(plain.CompressedText);
        Assert.Equal("Yes", compressed.ChecksumText);
        Assert.Equal("Damaged", (compressed with { IsDamaged = true }).ChecksumText);
    }

    // ── The queries ──────────────────────────────────────────────────────────

    [Fact]
    public void DatabasesSql_skips_tempdb_and_snapshots_and_only_counts_backups_since_create_date()
    {
        var sql = BackupRepository.DatabasesSql;

        Assert.Contains("d.database_id <> 2", sql);
        Assert.Contains("d.source_database_id IS NULL", sql);
        Assert.Contains("b.backup_finish_date >= d.create_date", sql);
        Assert.Contains("b.type IN ('D', 'I', 'L')", sql);
        Assert.Contains("COLLATE DATABASE_DEFAULT", sql);
        Assert.Contains("last_log_backup_lsn", sql);
        // backupset is read once, not once per backup type.
        Assert.Equal(1, Count(sql, "msdb.dbo.backupset"));
    }

    [Fact]
    public void The_edition_check_reads_nothing_Azure_SQL_Database_lacks()
    {
        Assert.DoesNotContain("msdb", BackupRepository.ServerSql);
        Assert.Contains("EngineEdition", BackupRepository.ServerSql);
    }

    [Fact]
    public void HistorySql_is_capped_and_newest_first()
    {
        Assert.Contains("TOP (@maxRows)", BackupRepository.HistorySql);
        Assert.Contains("ORDER BY b.backup_finish_date DESC", BackupRepository.HistorySql);
    }

    private static int Count(string text, string part)
    {
        int count = 0, index = 0;
        while ((index = text.IndexOf(part, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += part.Length;
        }
        return count;
    }
}
