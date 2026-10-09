using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Engine.IntegrationTests;

/// <summary>
/// Sets the server up once for every test (#46): the SqlVitalsIT database (Setup/Database.sql), a
/// workload that fills the plan cache and Query Store, a forced plan, a deadlock, two Agent jobs
/// that have run, and a blocking chain held open until the tests end: a session in a transaction
/// (with a temp table) and one waiting on its lock.
/// </summary>
public sealed partial class SqlServerFixture : IAsyncLifetime
{
    public const string Database = "SqlVitalsIT";

    private static readonly TimeSpan ServerStartWait = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan AgentWait = TimeSpan.FromMinutes(2);

    private SqlConnection? _blocker;
    private SqlTransaction? _blockerTransaction;
    private SqlConnection? _waiter;
    private SqlCommand? _wait;
    private Task? _waiting;

    /// <summary>The SqlVitalsIT database, as the app would connect to it.</summary>
    public SqlTarget Target { get; private set; } = new(string.Empty);

    /// <summary>The session holding Held's row lock in an open transaction: a sleeping head blocker.</summary>
    public int BlockerSessionId { get; private set; }

    /// <summary>The session waiting on <see cref="BlockerSessionId"/>'s lock: a running request.</summary>
    public int WaiterSessionId { get; private set; }

    /// <summary>Whether SQL Server Agent ran the jobs; false where there is no Agent (Express).</summary>
    public bool AgentRan { get; private set; }

    /// <summary>The server's major version: 14 for SQL Server 2017 … 17 for 2025.</summary>
    public int MajorVersion { get; private set; }

    /// <summary>The repositories the app uses for one connection, with their own capabilities.</summary>
    public IWaitStatsRepository NewRepository() => new WaitStatsRepository(Target);

    public async Task InitializeAsync()
    {
        if (TestServer.ConnectionString is not { } connectionString)
        {
            if (TestServer.Required)
                throw new InvalidOperationException($"{TestServer.Variable} isn't set: the workflow must give the tests a server.");
            return;
        }

        var master = new SqlConnectionStringBuilder(connectionString)
        {
            InitialCatalog = "master",
            ApplicationName = "SqlVitals integration tests",
        }.ConnectionString;
        var database = new SqlConnectionStringBuilder(master) { InitialCatalog = Database }.ConnectionString;

        await WaitForServerAsync(master);
        await RunScriptAsync(master, "Database.sql");
        Target = new SqlTarget(database);
        MajorVersion = Convert.ToInt32(await ScalarAsync(master, "SELECT CAST(SERVERPROPERTY('ProductMajorVersion') AS int)"), CultureInfo.InvariantCulture);

        await RunWorkloadAsync(database);
        await ForcePlanAsync(database);
        await DeadlockAsync(database);
        AgentRan = await RunAgentJobsAsync(master);
        await HoldBlockingChainAsync(database);
    }

    public async Task DisposeAsync()
    {
        if (_wait is not null && _waiting is not null)
        {
            _wait.Cancel();
            try { await _waiting; }
            catch (SqlException) { /* cancelled, as asked */ }
            await _wait.DisposeAsync();
        }
        if (_blockerTransaction is not null)
            await _blockerTransaction.RollbackAsync();
        if (_waiter is not null)
            await _waiter.DisposeAsync();
        if (_blocker is not null)
            await _blocker.DisposeAsync();
    }

    // ── The workload ─────────────────────────────────────────────────

    /// <summary>
    /// Calls each procedure as an RPC <paramref name="times"/> times, as an application would:
    /// the plan cache's procedure stats, Query Store and SP Trace's rpc_completed see them.
    /// </summary>
    public async Task RunProceduresAsync(int times)
    {
        await using var conn = await OpenAsync(Target.ConnectionString);
        for (int i = 0; i < times; i++)
        {
            var code = $"C{i % 500 + 1:00000}";
            await ProcedureAsync(conn, "dbo.GetCustomerOrders", ("@Code", SqlDbType.VarChar, code));
            await ProcedureAsync(conn, "dbo.FindOrdersByCode", ("@Code", SqlDbType.NVarChar, code));
            await ProcedureAsync(conn, "dbo.OrdersOfAmount", ("@Amount", SqlDbType.Decimal, (decimal)(i % 1000)));
        }
    }

    private async Task RunWorkloadAsync(string database)
    {
        await RunProceduresAsync(30);

        await using var conn = await OpenAsync(database);
        // The same statement with different literals: one query hash, several cached plans.
        for (int i = 1; i <= 8; i++)
            await ExecuteAsync(conn,
                "SELECT c.Name, SUM(o.Amount) AS Total FROM dbo.Orders o JOIN dbo.Customers c ON c.CustomerId = o.CustomerId " +
                $"WHERE c.Code = 'C{i:00000}' AND o.Amount > {i * 10} GROUP BY c.Name");
        // The plan guide's statement, and a scan the stale statistics and missing index come from.
        await ExecuteAsync(conn, "SELECT COUNT(*) FROM dbo.Orders WHERE Amount > 100");
        await ExecuteAsync(conn, "SELECT COUNT(*) FROM dbo.Events");
        // IX_Orders_Unused is written to, never read: a seek on the clustered key finds the rows (a
        // filter on OrderId % n scans that index instead, as it is narrower than the table).
        await ExecuteAsync(conn, "UPDATE dbo.Orders SET Notes = 'changed' WHERE OrderId BETWEEN 1 AND 400");
    }

    /// <summary>Forces GetCustomerOrders' plan in Query Store, and on 2022+ gives a query a hint.</summary>
    private static async Task ForcePlanAsync(string database)
    {
        await using var conn = await OpenAsync(database);
        await ExecuteAsync(conn, """
            DECLARE @query bigint, @plan bigint;
            SELECT TOP (1) @query = q.query_id, @plan = p.plan_id
            FROM sys.query_store_query q
            JOIN sys.query_store_plan p ON p.query_id = q.query_id
            WHERE q.object_id = OBJECT_ID(N'dbo.GetCustomerOrders')
            ORDER BY p.plan_id;
            IF @query IS NULL
                THROW 50000, N'Query Store has no plan for dbo.GetCustomerOrders.', 1;
            EXEC sys.sp_query_store_force_plan @query, @plan;

            IF OBJECT_ID(N'sys.query_store_query_hints') IS NOT NULL
            BEGIN
                SELECT TOP (1) @query = q.query_id
                FROM sys.query_store_query q
                WHERE q.object_id = OBJECT_ID(N'dbo.OrdersOfAmount');
                EXEC sys.sp_query_store_set_hints @query_id = @query, @query_hints = N'OPTION (MAXDOP 1)';
            END
            """);
    }

    /// <summary>
    /// Two sessions update LockA and LockB in opposite orders; SQL Server kills one (1205), and
    /// system_health records the deadlock.
    /// </summary>
    private static async Task DeadlockAsync(string database)
    {
        await using var a = await OpenAsync(database);
        await using var b = await OpenAsync(database);
        await using var ta = (SqlTransaction)await a.BeginTransactionAsync();
        await using var tb = (SqlTransaction)await b.BeginTransactionAsync();

        await ExecuteAsync(a, "UPDATE dbo.LockA SET V = V + 1", ta);
        await ExecuteAsync(b, "UPDATE dbo.LockB SET V = V + 1", tb);
        var first = ExecuteAsync(a, "UPDATE dbo.LockB SET V = V + 1", ta);
        await Task.Delay(500);
        var second = ExecuteAsync(b, "UPDATE dbo.LockA SET V = V + 1", tb);

        int victims = 0;
        foreach (var update in new[] { first, second })
        {
            try { await update; }
            catch (SqlException ex) when (ex.Number == 1205) { victims++; }
        }
        Assert.Equal(1, victims);

        foreach (var transaction in new[] { ta, tb })
        {
            try { await transaction.RollbackAsync(); }
            catch (InvalidOperationException) { /* the victim's was rolled back by the server */ }
        }
    }

    /// <summary>
    /// Runs Setup/AgentJobs.sql and both jobs, and waits for their outcome rows. False when the
    /// server has no Agent running (the Agent XPs option is off).
    /// </summary>
    private static async Task<bool> RunAgentJobsAsync(string master)
    {
        var agentXps = await ScalarAsync(master, "SELECT CAST(value_in_use AS int) FROM sys.configurations WHERE name = N'Agent XPs'");
        if (agentXps is not int on || on != 1)
            return false;

        await RunScriptAsync(master, "AgentJobs.sql");
        await using var conn = await OpenAsync(master);
        var clock = Stopwatch.StartNew();
        foreach (var job in new[] { "SqlVitalsIT succeeds", "SqlVitalsIT fails" })
        {
            // Agent may still be starting with the container: then sp_start_job says it isn't running.
            while (true)
            {
                try
                {
                    await ExecuteAsync(conn, $"EXEC msdb.dbo.sp_start_job @job_name = N'{job}'");
                    break;
                }
                catch (SqlException) when (clock.Elapsed < AgentWait)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }
        }

        while (true)
        {
            var finished = await ScalarAsync(master, """
                SELECT COUNT(DISTINCT h.job_id) FROM msdb.dbo.sysjobhistory h
                JOIN msdb.dbo.sysjobs j ON j.job_id = h.job_id
                WHERE h.step_id = 0 AND j.name LIKE N'SqlVitalsIT %'
                """);
            if (Convert.ToInt32(finished, CultureInfo.InvariantCulture) == 2)
                return true;
            if (clock.Elapsed > AgentWait)
                throw new TimeoutException("SQL Server Agent didn't run the jobs within two minutes.");
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    /// <summary>
    /// The blocker fills a temp table and takes Held's row lock in a transaction it leaves open;
    /// the waiter's SELECT waits on it until the tests end.
    /// </summary>
    private async Task HoldBlockingChainAsync(string database)
    {
        _blocker = await OpenAsync(database);
        await ExecuteAsync(_blocker, """
            CREATE TABLE #work (Id int NOT NULL, Filler char(500) NOT NULL);
            INSERT #work SELECT TOP (2000) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), 'x' FROM sys.all_columns;
            """);
        _blockerTransaction = (SqlTransaction)await _blocker.BeginTransactionAsync();
        await ExecuteAsync(_blocker, "UPDATE dbo.Held SET V = V + 1 WHERE Id = 1", _blockerTransaction);
        BlockerSessionId = Convert.ToInt32(await ScalarAsync(_blocker, "SELECT @@SPID", _blockerTransaction), CultureInfo.InvariantCulture);

        _waiter = await OpenAsync(database);
        WaiterSessionId = Convert.ToInt32(await ScalarAsync(_waiter, "SELECT @@SPID"), CultureInfo.InvariantCulture);
        _wait = _waiter.CreateCommand();
        _wait.CommandText = "SELECT V FROM dbo.Held WHERE Id = 1";
        _wait.CommandTimeout = 0;
        _waiting = _wait.ExecuteScalarAsync();

        var clock = Stopwatch.StartNew();
        while (Convert.ToInt32(await ScalarAsync(database,
                   $"SELECT COUNT(*) FROM sys.dm_exec_requests WHERE session_id = {WaiterSessionId} AND blocking_session_id = {BlockerSessionId}"), CultureInfo.InvariantCulture) == 0)
        {
            if (clock.Elapsed > TimeSpan.FromSeconds(30))
                throw new TimeoutException("The waiting session never blocked.");
            await Task.Delay(200);
        }
    }

    /// <summary>A query's rows as text, for a failed assertion's message: what the server holds.</summary>
    public async Task<string> DescribeAsync(string sql)
    {
        await using var conn = await OpenAsync(Target.ConnectionString);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 60 };
        await using var reader = await cmd.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(string.Join(" | ", Enumerable.Range(0, reader.FieldCount).Select(i => $"{reader.GetName(i)}={reader.GetValue(i)}")));
        return rows.Count == 0 ? $"no rows from: {sql}" : string.Join(Environment.NewLine, rows);
    }

    // ── Plumbing ─────────────────────────────────────────────────────

    /// <summary>A container takes a while to accept logins after it starts.</summary>
    private static async Task WaitForServerAsync(string master)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                await ScalarAsync(master, "SELECT 1");
                return;
            }
            catch (SqlException) when (clock.Elapsed < ServerStartWait)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
    }

    /// <summary>Runs a file from Setup/ batch by batch, split on its GO lines.</summary>
    private static async Task RunScriptAsync(string connectionString, string file)
    {
        var script = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Setup", file));
        await using var conn = await OpenAsync(connectionString);
        foreach (var batch in GoLine().Split(script).Where(b => !string.IsNullOrWhiteSpace(b)))
            await ExecuteAsync(conn, batch);
    }

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex GoLine();

    private static async Task<SqlConnection> OpenAsync(string connectionString)
    {
        var conn = new SqlConnection(connectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task ExecuteAsync(SqlConnection conn, string sql, SqlTransaction? transaction = null)
    {
        await using var cmd = new SqlCommand(sql, conn, transaction) { CommandTimeout = 300 };
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<object?> ScalarAsync(SqlConnection conn, string sql, SqlTransaction? transaction = null)
    {
        await using var cmd = new SqlCommand(sql, conn, transaction) { CommandTimeout = 300 };
        var value = await cmd.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var conn = await OpenAsync(connectionString);
        return await ScalarAsync(conn, sql);
    }

    private static async Task ProcedureAsync(SqlConnection conn, string name, (string Name, SqlDbType Type, object Value) parameter)
    {
        await using var cmd = new SqlCommand(name, conn) { CommandType = CommandType.StoredProcedure, CommandTimeout = 300 };
        var p = cmd.Parameters.Add(parameter.Name, parameter.Type);
        if (parameter.Type is SqlDbType.VarChar or SqlDbType.NVarChar)
            p.Size = 20;
        if (parameter.Type is SqlDbType.Decimal)
            (p.Precision, p.Scale) = (12, 2);
        p.Value = parameter.Value;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { }
    }
}

/// <summary>Every test class that reads the server shares one fixture, and runs one at a time.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerTestGroup : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "SQL Server";
}
