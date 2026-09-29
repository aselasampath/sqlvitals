using System.Globalization;
using SqlVitals.Engine.Models;
using SqlVitals.Engine.Scripting;

namespace SqlVitals.Engine.QueryStore;

public enum QueryStoreCheckLevel { Ok, Info, Warning }

/// <summary>One Query Store setting that needs a look: why, and the ALTER DATABASE to copy (null when nothing in Query Store would fix it).</summary>
public sealed record QueryStoreCheck(QueryStoreCheckLevel Level, string Title, string Reason, string? Fix)
{
    public string LevelText => Level == QueryStoreCheckLevel.Warning ? "Warning" : "Info";
}

/// <summary>
/// Checks the database's Query Store settings (#111). Each finding says why in one line and gives
/// the fix to copy; SqlVitals never changes a setting itself. The Status card shows the worst.
/// </summary>
public static class QueryStoreChecks
{
    /// <summary>Storage used above this share of MAX_STORAGE_SIZE_MB is a warning, as the page always had.</summary>
    public const double StorageWarningPct = 90;

    // readonly_reason bits that are the database's state, not Query Store's: nothing in Query Store fixes them.
    private const int DatabaseStateReasons = 1 | 2 | 4 | 8;

    private const int StorageFullReason = 65536;

    public static IReadOnlyList<QueryStoreCheck> Run(QueryStoreHealth h)
    {
        var db     = UnusedIndexDropScript.QuoteName(h.DatabaseName);
        var checks = new List<QueryStoreCheck>();

        if (h.ActualState == "OFF" && h.DesiredState == "OFF")
        {
            checks.Add(new(QueryStoreCheckLevel.Warning, "Query Store is off",
                $"Query Store is off for {h.DatabaseName}, so it records no queries.",
                $"ALTER DATABASE {db} SET QUERY_STORE = ON;"));
            return checks;
        }

        if (!string.Equals(h.ActualState, h.DesiredState, StringComparison.OrdinalIgnoreCase))
            checks.Add(StateMismatch(h, db));

        if (!h.IsEnabled)
            return checks;

        if (h.StorageUsedPct > StorageWarningPct && (h.ReadOnlyReasonCode & StorageFullReason) == 0)
            checks.Add(new(QueryStoreCheckLevel.Warning, "Storage nearly full",
                string.Format(CultureInfo.CurrentCulture,
                    "Query Store uses {0:N0} of {1:N0} MB ({2:N1} %). When it's full it turns read-only and stops recording.",
                    h.CurrentStorageSizeMB, h.MaxStorageSizeMB, h.StorageUsedPct),
                $"ALTER DATABASE {db} SET QUERY_STORE (MAX_STORAGE_SIZE_MB = {SuggestedMaxStorageMb(h)});"));

        if (string.Equals(h.SizeCleanupMode, "OFF", StringComparison.OrdinalIgnoreCase))
            checks.Add(new(QueryStoreCheckLevel.Warning, "Size-based cleanup is off",
                "Query Store turns read-only when it fills up, instead of removing its oldest, cheapest queries.",
                $"ALTER DATABASE {db} SET QUERY_STORE (SIZE_BASED_CLEANUP_MODE = AUTO);"));

        if (string.Equals(h.CaptureMode, "ALL", StringComparison.OrdinalIgnoreCase))
            checks.Add(new(QueryStoreCheckLevel.Info, "Capture mode is ALL",
                "AUTO, the default from SQL Server 2019, skips trivial ad hoc queries and saves space.",
                $"ALTER DATABASE {db} SET QUERY_STORE (QUERY_CAPTURE_MODE = AUTO);"));

        if (string.Equals(h.WaitStatsCaptureMode, "OFF", StringComparison.OrdinalIgnoreCase))
            checks.Add(new(QueryStoreCheckLevel.Info, "Wait stats capture is off",
                "The Waits tab stays empty until it's on.",
                $"ALTER DATABASE {db} SET QUERY_STORE (WAIT_STATS_CAPTURE_MODE = ON);"));

        return checks;
    }

    /// <summary>The worst level among the checks; Ok when there are none.</summary>
    public static QueryStoreCheckLevel Worst(IEnumerable<QueryStoreCheck> checks) =>
        checks.Select(c => c.Level).DefaultIfEmpty(QueryStoreCheckLevel.Ok).Max();

    // Doubled: enough room for a while without guessing the workload.
    private static long SuggestedMaxStorageMb(QueryStoreHealth h) => Math.Max(100, h.MaxStorageSizeMB * 2);

    private static QueryStoreCheck StateMismatch(QueryStoreHealth h, string db)
    {
        var title = $"Should be {h.DesiredState} but is {h.ActualState}";

        if (h.ActualState == "ERROR")
            return new(QueryStoreCheckLevel.Warning, title,
                "Query Store hit an internal error and records nothing. The consistency check (SQL Server 2017+) usually recovers it; " +
                "if it doesn't, SET QUERY_STORE CLEAR ALL starts it again empty.",
                $"USE {db};\nEXEC sys.sp_query_store_consistency_check;\nALTER DATABASE {db} SET QUERY_STORE (OPERATION_MODE = READ_WRITE);");

        var reason = DescribeReadOnlyReason(h.ReadOnlyReasonCode);
        if (h.ActualState == "READ_ONLY" && reason is not null)
        {
            // A read-only database or a readable secondary: Query Store follows the database.
            if ((h.ReadOnlyReasonCode & ~DatabaseStateReasons) == 0)
                return new(QueryStoreCheckLevel.Info, title,
                    $"Read-only because {reason}. It follows the database, so there is nothing to change in Query Store.", null);

            var fix = (h.ReadOnlyReasonCode & StorageFullReason) != 0
                ? $"ALTER DATABASE {db} SET QUERY_STORE (MAX_STORAGE_SIZE_MB = {SuggestedMaxStorageMb(h)}, OPERATION_MODE = READ_WRITE);"
                : $"ALTER DATABASE {db} SET QUERY_STORE (OPERATION_MODE = READ_WRITE);";
            return new(QueryStoreCheckLevel.Warning, title, $"Read-only because {reason}, so it records nothing new.", fix);
        }

        return new(QueryStoreCheckLevel.Warning, title,
            $"Query Store isn't in the mode it was set to, so it may not be recording.",
            $"ALTER DATABASE {db} SET QUERY_STORE (OPERATION_MODE = {(h.DesiredState == "READ_ONLY" ? "READ_ONLY" : "READ_WRITE")});");
    }

    /// <summary>
    /// sys.database_query_store_options.readonly_reason, a bit map of why Query Store went read-only,
    /// in words; null when it isn't read-only for any reason (0).
    /// </summary>
    public static string? DescribeReadOnlyReason(int reason)
    {
        if (reason == 0) return null;

        (int Bit, string Text)[] reasons =
        [
            (1,      "the database is read-only"),
            (2,      "the database is in single-user mode"),
            (4,      "the database is in emergency mode"),
            (8,      "the database is a secondary replica"),
            (65536,  "Query Store reached MAX_STORAGE_SIZE_MB"),
            (131072, "the number of different statements reached Query Store's internal memory limit"),
            (262144, "the in-memory items waiting to be written to disk reached Query Store's internal memory limit"),
            (524288, "the database reached its disk size limit"),
        ];
        var known = reasons.Where(r => (reason & r.Bit) != 0).Select(r => r.Text).ToList();
        var other = reason & ~reasons.Aggregate(0, (all, r) => all | r.Bit);
        if (other != 0)
            known.Add($"reason code {other}");
        return string.Join("; ", known);
    }
}
