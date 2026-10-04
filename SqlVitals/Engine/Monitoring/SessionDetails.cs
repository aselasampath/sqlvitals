using System.Globalization;
using SqlVitals.Engine.Alerts;
using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Monitoring;

/// <summary>One line of a details drawer: "Login · svc_orders".</summary>
/// <param name="Warning">Shown in amber: blocking others, a transaction left open.</param>
public sealed record DetailRow(string Label, string Value, bool Warning = false);

/// <summary>
/// What the details drawer says about a session on Processes (#147): who it is, what it waits on
/// or blocks, how long its transaction has been open, and a KILL script to review. Pure.
/// </summary>
public static class SessionDetails
{
    /// <summary>A transaction open this long while its session sleeps is shown as a warning.</summary>
    public static readonly TimeSpan LongTransaction = TimeSpan.FromMinutes(1);

    /// <summary>How long its oldest transaction has been open, on the server's clock; null with none.</summary>
    public static TimeSpan? TransactionAge(ProcessNode node) =>
        node.TransactionBegin is { } begin && node.ServerNow is { } now && now >= begin ? now - begin : null;

    /// <param name="snapshot">Every session read with it, as <see cref="BlockingChains.Classify"/> leaves them.</param>
    public static IReadOnlyList<DetailRow> Rows(ProcessNode node, IReadOnlyList<ProcessNode> snapshot)
    {
        var culture = CultureInfo.CurrentCulture;
        var rows    = new List<DetailRow>();
        var sleeping = string.Equals(node.Status, "sleeping", StringComparison.OrdinalIgnoreCase);

        rows.Add(new("Status", node.OpenTran switch
        {
            0     => node.Status,
            1     => $"{node.Status} · 1 open transaction",
            var n => $"{node.Status} · {n} open transactions",
        }, Warning: sleeping && node.OpenTran > 0));

        // Blocking: how many sessions wait behind it, at any depth, and the longest of their waits.
        var chain = BlockingChains.Find(snapshot).FirstOrDefault(c => c.HeadSessionId == node.SessionId);
        var directly = BlockingChains.BlockedDirectly(snapshot).GetValueOrDefault(node.SessionId);
        if (chain is not null)
            rows.Add(new("Blocking", $"{Sessions(chain.BlockedCount)} · longest {Wait(chain.LongestWaitMs)}", Warning: true));
        else if (directly > 0)
            rows.Add(new("Blocking", $"{Sessions(directly)} directly", Warning: true));

        if (node.ParentId != 0)
        {
            var head = BlockingChains.HeadOf(snapshot, node.SessionId);
            rows.Add(new("Blocked by", node.ParentId > 0 && head != node.ParentId && head != node.SessionId
                ? $"session {node.ParentId} · head of chain {head}"
                : BlockingChains.DescribeBlocker(node.ParentId).Replace("SPID ", "session ", StringComparison.Ordinal)));
        }

        if (!string.IsNullOrWhiteSpace(node.WaitType))
            rows.Add(new("Waiting", string.IsNullOrWhiteSpace(node.WaitResource)
                ? $"{node.WaitType} · {Wait(node.WaitTimeMs)}"
                : $"{node.WaitType} · {Wait(node.WaitTimeMs)} · {node.WaitResource}"));

        if (TransactionAge(node) is { } age)
            rows.Add(new("Transaction open", $"{AlertText.Duration(age)} · since {node.TransactionBegin:HH:mm:ss}",
                         Warning: sleeping && age >= LongTransaction));

        Add("Login",    node.UserName);
        Add("Host",     node.HostName);
        Add("Program",  node.ProgramName);
        Add("Database", node.DatabaseName);
        Add("Command",  node.Command);

        if (node.LastBatch > DateTime.MinValue)
        {
            var idle = sleeping && node.ServerNow is { } now && now >= node.LastBatch
                ? $" · idle {AlertText.Duration(now - node.LastBatch)}"
                : "";
            rows.Add(new("Last batch ended", node.LastBatch.ToString("HH:mm:ss", culture) + idle));
        }
        if (node.LoginTime > DateTime.MinValue)
            rows.Add(new("Logged in", node.LoginTime.ToString("yyyy-MM-dd HH:mm:ss", culture)));

        rows.Add(new("CPU · I/O · memory",
            string.Format(culture, "{0:N0} ms · {1:N0} · {2:N0} pages", node.CpuTime, node.PhysicalIo, node.MemoryUsage)));
        return rows;

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                rows.Add(new(label, value.Trim()));
        }
    }

    /// <summary>
    /// A KILL statement for the session, with what to check first, to review and run elsewhere.
    /// SqlVitals never runs it.
    /// </summary>
    public static string KillScript(ProcessNode node, IReadOnlyList<ProcessNode> snapshot)
    {
        var id    = node.SessionId.ToString(CultureInfo.InvariantCulture);
        var lines = new List<string>
        {
            "-- Review before running: SqlVitals never runs this.",
            "-- " + string.Join(" · ", new[]
            {
                $"Session {id}",
                Named("login", node.UserName), Named("host", node.HostName),
                Named("program", node.ProgramName), Named("database", node.DatabaseName),
            }.Where(p => p.Length > 0)),
        };

        var chain = BlockingChains.Find(snapshot).FirstOrDefault(c => c.HeadSessionId == node.SessionId);
        if (chain is not null)
            lines.Add($"-- It is blocking {Sessions(chain.BlockedCount)}, the longest waiting {Wait(chain.LongestWaitMs)}.");

        if (TransactionAge(node) is { } age)
            lines.Add($"-- Its open transaction began at {node.TransactionBegin:HH:mm:ss} ({AlertText.Duration(age)} ago). " +
                      "KILL rolls it back, and the rollback can take as long as the work did.");
        else if (node.OpenTran > 0)
            lines.Add("-- It has an open transaction: KILL rolls it back, and the rollback can take as long as the work did.");

        if (node.SessionId <= 50)
            lines.Add($"-- Session {id} may be a system session, which can't be killed.");

        lines.Add("");
        lines.Add("-- What it ran last:");
        lines.Add($"DBCC INPUTBUFFER({id});");
        lines.Add("");
        lines.Add($"KILL {id};");
        lines.Add("");
        lines.Add("-- How far its rollback has got:");
        lines.Add($"KILL {id} WITH STATUSONLY;");
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;

        static string Named(string what, string? value) => string.IsNullOrWhiteSpace(value) ? "" : $"{what} {value.Trim()}";
    }

    private static string Sessions(int count) => count == 1 ? "1 session" : $"{count:N0} sessions";

    private static string Wait(long ms) => AlertText.Value(HealthIndicator.Blocking, ms / 1000.0);
}
