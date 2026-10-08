using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.SqlClient;

namespace SqlVitals.Engine.Tests.Repositories;

/// <summary>One command a repository ran: its text, time limit, and the token Dapper handed it.</summary>
internal sealed record RecordedCommand(string Sql, int Timeout, CancellationToken Token);

/// <summary>
/// A connection that runs nothing (#167): it records each command a repository sends, answers it
/// with the table <see cref="Answer"/> gives for its text, and honours the command's token, so a
/// test can see what reaches the server without one.
/// </summary>
[SuppressMessage("Design", "CA2000", Justification = "The commands are the test's to inspect; nothing is held open")]
internal sealed class RecordingConnection : DbConnection
{
    private ConnectionState _state = ConnectionState.Closed;

    public List<RecordedCommand> Commands { get; } = [];

    /// <summary>The rows for a command's text; null answers with no rows.</summary>
    public Func<string, DataTable?> Answer { get; init; } = _ => null;

    /// <summary>A command whose text contains this waits until it is cancelled, as a long query would.</summary>
    public string? Hangs { get; init; }

    /// <summary>A command whose text contains this fails with the exception.</summary>
    public (string Sql, Exception Error)? Fails { get; init; }

    [AllowNull]
    public override string ConnectionString { get; set; } = "";
    public override string Database => "Sales";
    public override string DataSource => "sql01";
    public override string ServerVersion => "16.00.4135";
    public override ConnectionState State => _state;

    public override void Open() => _state = ConnectionState.Open;
    public override void Close() => _state = ConnectionState.Closed;
    public override void ChangeDatabase(string databaseName) { }
    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    protected override DbCommand CreateDbCommand() => new RecordingCommand(this);

    /// <summary>A table of one row: the columns and their values in pairs.</summary>
    public static DataTable Row(params (string Column, object Value)[] values)
    {
        var table = new DataTable();
        foreach (var (column, value) in values)
            table.Columns.Add(column, value.GetType());
        table.Rows.Add(values.Select(v => v.Value).ToArray());
        return table;
    }

    /// <summary>A table of one column with a row per value.</summary>
    public static DataTable Column(string column, params string[] values)
    {
        var table = new DataTable();
        table.Columns.Add(column, typeof(string));
        foreach (var value in values)
            table.Rows.Add(value);
        return table;
    }

    private sealed class RecordingCommand(RecordingConnection connection) : DbCommand
    {
        // SqlClient's own parameter collection, so Dapper's parameters work as they do against a server.
        private readonly SqlCommand _parameters = new();

        [AllowNull]
        public override string CommandText { get; set; } = "";
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbParameterCollection DbParameterCollection => _parameters.Parameters;
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new SqlParameter();

        public override int ExecuteNonQuery() => ExecuteNonQueryAsync(CancellationToken.None).GetAwaiter().GetResult();
        public override object? ExecuteScalar() => null;
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            ExecuteDbDataReaderAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();

        public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            await RunAsync(cancellationToken);
            return 0;
        }

        protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
        {
            await RunAsync(cancellationToken);
            return (connection.Answer(CommandText) ?? new DataTable()).CreateDataReader();
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            connection.Commands.Add(new RecordedCommand(CommandText, CommandTimeout, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            if (connection.Hangs is { } hangs && CommandText.Contains(hangs, StringComparison.Ordinal))
                await Task.Delay(Timeout.Infinite, cancellationToken);
            if (connection.Fails is { } fails && CommandText.Contains(fails.Sql, StringComparison.Ordinal))
                throw fails.Error;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _parameters.Dispose();
            base.Dispose(disposing);
        }
    }
}
