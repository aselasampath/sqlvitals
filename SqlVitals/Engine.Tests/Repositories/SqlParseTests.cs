using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using SqlVitals.Engine.QueryStore;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Scripting;

namespace SqlVitals.Engine.Tests.Repositories;

/// <summary>
/// Every embedded query parses under SQL Server 2016's grammar (the oldest version SqlVitals reads)
/// and 2022's (#168). There is no SQL Server in the tests, so the parser is what catches a typo
/// before a user does. A template is parsed once per combination of what its markers can be
/// replaced with, so each version branch (a column a server lacks, Azure's view) is parsed too.
/// A new .sql file is picked up on its own; a new marker needs its values added to <see cref="Markers"/>.
/// </summary>
public partial class SqlParseTests
{
    // What each /*MARKER*/ becomes, as the repositories fill it: every alternative they choose
    // between. Shared fragments come from the repositories themselves.
    private static readonly Dictionary<string, string[]> Markers = new(StringComparer.Ordinal)
    {
        // BaseRepository.HasColumnSql
        ["VIEW"]               = ["sys.dm_exec_requests"],
        ["COLUMN"]             = ["dop", "duration", "log_bytes_used"],   // also QueryStore/HighVariation
        // History
        ["WAIT_STATS"]         = ["sys.dm_os_wait_stats", "sys.dm_db_wait_stats"],
        ["BENIGN_WAITS"]       = [HistoryRepository.BenignWaits],
        ["COUNTER_FILTER"]     = [LiveMetricsRepository.PerfmonCounterFilter],
        // Index, LiveMetrics, Memory, Storage, Waits
        ["INCREMENTAL"]        = ["s.is_incremental", "CAST(0 AS BIT)"],
        ["CPU"]                = [LiveMetricsRepository.RingBufferCpuSql, LiveMetricsRepository.AzureSqlDbCpuSql],
        ["MEMORY_MODEL"]       = [MemoryRepository.MemoryModelColumnSql, MemoryRepository.MemoryModelUnknownSql],
        ["QUERY_STORE_ON"]     = ["d.is_query_store_on", "0"],
        ["DOP"]                = ["r.dop", "0"],
        // Query Store
        ["PERIOD"]             = [QueryStoreRepository.PeriodSql],
        ["RUNTIME_IN_PERIOD"]  = [QueryStoreRepository.RuntimeInPeriod],
        ["RANKED_PLANS"]       = [QueryStoreRepository.RankedPlans],
        ["OBJECT_NAME"]        = [QueryStoreRepository.ObjectNameSql],
        ["STDEV"]              = [QueryStoreRepository.StdDevSql("m_sum", "m_sum_sq", "n")],
        ["TOTALS"]             = [QueryStoreRepository.TotalsSql(false, false), QueryStoreRepository.TotalsSql(true, true)],
        ["METRIC_SUMS"]        = [QueryStoreRepository.MetricSumsSql(null), QueryStoreRepository.MetricSumsSql("duration")],
        ["RANK"]               = [.. Enum.GetValues<QueryStoreStatistic>().Select(s => QueryStoreRepository.RankSql("duration", s)),
                                  QueryStoreRepository.RankSql(null, QueryStoreStatistic.Total)],
        ["LOG_BYTES"]          = ["SUM(rs.avg_log_bytes_used * rs.count_executions)", "CAST(0 AS float)"],
        ["TEMPDB"]             = ["SUM(rs.avg_tempdb_space_used * rs.count_executions)", "CAST(0 AS float)"],
        ["WAIT_STATS_CAPTURE"] = ["wait_stats_capture_mode_desc", "CAST(NULL AS nvarchar(60))"],
        ["PLAN_TYPE"]          = ["p.plan_type_desc", "N'Compiled Plan'"],
        ["FORCING_TYPE"]       = ["p.plan_forcing_type_desc", "N'MANUAL'"],
        // SpTrace: the session, and what it's created with
        ["SESSION"]            = ["SqlVitals_SpTrace"],
        ["SCOPE"]              = ["SERVER", "DATABASE"],
        ["CATALOG"]            = ["sys.server_event_sessions", "sys.database_event_sessions"],
        ["SESSIONS"]           = ["sys.dm_xe_sessions", "sys.dm_xe_database_sessions"],
        ["TARGETS"]            = ["sys.dm_xe_session_targets", "sys.dm_xe_database_session_targets"],
        ["ACTIONS"]            = ["sqlserver.session_id, sqlserver.database_name, sqlserver.username, " +
                                  "sqlserver.client_app_name, sqlserver.client_hostname"],
        ["RPC_PREDICATE"]      = ["duration >= 100000", "duration >= 100000 AND sqlserver.database_id = 5"],
        ["MODULE_PREDICATE"]   = ["duration >= 100000 AND object_type = 8272", "duration >= 100000 AND sqlserver.database_id = 5"],
        ["MAX_MEMORY_KB"]      = ["4096"],
        ["MAX_EVENTS"]         = ["1000"],
        ["DB_FILTER"]          = ["", "WHERE ps.database_id = DB_ID()"],
        // SecurityAudit: the database the orphaned users are read in
        ["DATABASE"]           = [UnusedIndexDropScript.QuoteName("we]ird")],
    };

    // Not statements on their own: each is put into other queries through a marker, and parsed there.
    private static readonly string[] Fragments =
    [
        "History/BenignWaits",
        "LiveMetrics/PerfmonCounterFilter",
        "LiveMetrics/RingBufferCpu",
        "QueryStore/Period",
        "QueryStore/RuntimeInPeriod",
        "QueryStore/Totals",
    ];

    [GeneratedRegex(@"/\*([A-Z][A-Z0-9_]*)\*/")]
    private static partial Regex Marker();

    public static TheoryData<string, string> Statements()
    {
        var data = new TheoryData<string, string>();
        foreach (var name in SqlScripts.Names.Except(Fragments).Order(StringComparer.Ordinal))
            foreach (var grammar in new[] { "TSql130", "TSql160" })
                data.Add(name, grammar);
        return data;
    }

    [Theory]
    [MemberData(nameof(Statements))]
    public void Every_statement_parses(string name, string grammar)
    {
        TSqlParser parser = grammar == "TSql130" ? new TSql130Parser(true) : new TSql160Parser(true);

        foreach (var (filled, sql) in Variants(SqlScripts.Get(name)))
        {
            parser.Parse(new StringReader(sql), out var errors);
            Assert.True(errors.Count == 0,
                $"{name}{filled} under {grammar}: " + string.Join("; ", errors.Select(e => $"line {e.Line}: {e.Message}")));
        }
    }

    [Fact]
    public void Every_marker_has_its_values()
    {
        var unknown = SqlScripts.Names
            .SelectMany(name => Marker().Matches(SqlScripts.Get(name)).Select(m => $"/*{m.Groups[1].Value}*/ in {name}"))
            .Where(m => !Markers.ContainsKey(m[2..m.IndexOf('*', 2)]))
            .ToList();

        Assert.True(unknown.Count == 0, "Add to SqlParseTests.Markers: " + string.Join(", ", unknown));
    }

    [Fact]
    public void Every_fragment_is_parsed_inside_a_statement()
    {
        // Its first line shows up in a marker's values, so some template puts it into a statement.
        foreach (var fragment in Fragments)
        {
            var firstLine = SqlScripts.Get(fragment).Split('\n')[0].TrimEnd('\r');
            Assert.True(Markers.Values.Any(values => values.Any(v => v.Contains(firstLine, StringComparison.Ordinal))),
                $"{fragment} isn't any marker's value");
        }
    }

    [Fact]
    public void A_broken_statement_fails_to_parse()
    {
        // The parser does reject what a typo leaves behind, under both grammars.
        foreach (TSqlParser parser in new TSqlParser[] { new TSql130Parser(true), new TSql160Parser(true) })
        {
            parser.Parse(new StringReader("SELECT session_id, FROM sys.dm_exec_sessions"), out var errors);
            Assert.NotEmpty(errors);
        }
    }

    [Fact]
    public void A_template_is_parsed_once_per_combination_of_its_values()
    {
        var variants = Variants("SELECT /*DOP*/, /*INCREMENTAL*/ FROM x WHERE /*DOP*/ = 0").Select(v => v.Sql).ToList();

        Assert.Equal(4, variants.Count);
        Assert.Contains("SELECT r.dop, s.is_incremental FROM x WHERE r.dop = 0", variants);
        Assert.Contains("SELECT 0, CAST(0 AS BIT) FROM x WHERE 0 = 0", variants);
    }

    // Dapper sends "IN @ids" with a list as "IN (@ids1, @ids2, …)".
    [GeneratedRegex(@"\bIN\s+@(\w+)", RegexOptions.IgnoreCase)]
    private static partial Regex ListParameter();

    // The query as it's sent, with its markers replaced, once for each combination of their
    // values; a query without markers is itself. Filled names the choices, for the failure message.
    private static IEnumerable<(string Filled, string Sql)> Variants(string sql)
    {
        sql = ListParameter().Replace(sql, "IN (@${1}1, @${1}2)");
        IEnumerable<(string Filled, string Sql)> variants = [("", sql)];
        foreach (var marker in Marker().Matches(sql).Select(m => m.Groups[1].Value).Distinct())
        {
            var values = Markers.TryGetValue(marker, out var known) ? known : [];
            variants = variants.SelectMany(v => values.Select((value, i) =>
                (values.Length > 1 ? $"{v.Filled} {marker}={(value.Length <= 30 ? value : $"#{i}")}" : v.Filled,
                 v.Sql.Replace($"/*{marker}*/", value))));
        }
        return variants.Select(v => (v.Filled.Length > 0 ? $" ({v.Filled.Trim()})" : "", v.Sql));
    }
}
