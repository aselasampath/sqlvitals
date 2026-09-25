namespace SqlVitals.Engine.Deadlocks;

/// <summary>A lock one process holds on, or waits for, a deadlock resource.</summary>
/// <param name="ProcessId">The process id inside the report, e.g. <c>process1f2c3a8</c>.</param>
/// <param name="SessionId">The process's SPID, when the report names the process.</param>
/// <param name="Mode">Lock mode (X, U, S, IX…); empty for resources that aren't locks, like a parallel exchange.</param>
public sealed record DeadlockLock(string ProcessId, int? SessionId, string Mode)
{
    public string Text => (SessionId is { } spid ? $"SPID {spid}" : ProcessId) + (Mode.Length > 0 ? $" ({Mode})" : "");
}

/// <summary>
/// One resource in a deadlock's <c>resource-list</c>: a lock (key, page, row, object…) or
/// something else the processes waited on, such as a parallel exchange or the thread pool.
/// </summary>
/// <param name="Kind">Friendly kind: Key, Page, RID, Object, Parallel exchange…</param>
/// <param name="ObjectName">Table or other object, as the report names it (often <c>db.schema.table</c>).</param>
/// <param name="IndexName">Index, for key and page locks.</param>
/// <param name="Detail">What identifies the resource beyond the object: hobt id, page, wait type.</param>
public sealed record DeadlockResource(
    string  Kind,
    string? ObjectName,
    string? IndexName,
    string  Detail,
    IReadOnlyList<DeadlockLock> Owners,
    IReadOnlyList<DeadlockLock> Waiters)
{
    /// <summary>"db.dbo.Orders (PK_Orders)", or the kind when no object is named.</summary>
    public string ObjectText =>
        ObjectName is null ? Kind
        : IndexName is null ? ObjectName
        : $"{ObjectName} ({IndexName})";

    public string OwnersText  => string.Join(", ", Owners.Select(o => o.Text));
    public string WaitersText => string.Join(", ", Waiters.Select(w => w.Text));
}

/// <summary>One process (session) taking part in a deadlock.</summary>
public sealed record DeadlockProcess(
    string    Id,
    bool      IsVictim,
    int?      SessionId,
    string?   LoginName,
    string?   HostName,
    string?   ClientApp,
    string?   DatabaseName,
    string?   IsolationLevel,
    string?   LockMode,
    string?   WaitResource,
    long      WaitTimeMs,
    int       TranCount,
    string?   TransactionName,
    string?   ProcedureName,
    string?   Statement,
    string?   InputBuffer,
    DateTime? LastBatchStarted)
{
    /// <summary>"Victim" or empty, for the grid.</summary>
    public string Role => IsVictim ? "Victim" : string.Empty;

    public string SessionText => SessionId is { } spid ? $"SPID {spid}" : Id;

    /// <summary>The procedure it was in, else the first line of its statement.</summary>
    public string CodeText => ProcedureName ?? FirstLine(Statement ?? InputBuffer) ?? string.Empty;

    /// <summary>Filled in by the parser from the resource list: the locks it waited for.</summary>
    public string WaitingFor { get; init; } = string.Empty;

    /// <summary>Filled in by the parser from the resource list: the locks it held that others wanted.</summary>
    public string Holding { get; init; } = string.Empty;

    internal static string? FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var line = text.Trim().Split('\n', 2)[0].Trim();
        return line.Length <= 200 ? line : line[..200] + "…";
    }
}

/// <summary>
/// One <c>xml_deadlock_report</c> from the system_health Extended Events session (#38): who took
/// part, who was chosen as the victim, and the resources they fought over.
/// </summary>
/// <param name="TimestampUtc">When the deadlock monitor reported it.</param>
/// <param name="Xml">The <c>&lt;deadlock&gt;</c> element: the content of an .xdl file SSMS opens as a graph.</param>
public sealed record DeadlockReport(
    DateTime TimestampUtc,
    IReadOnlyList<DeadlockProcess>  Processes,
    IReadOnlyList<DeadlockResource> Resources,
    string Xml)
{
    public DateTime Time => TimestampUtc.ToLocalTime();

    public IEnumerable<DeadlockProcess> Victims => Processes.Where(p => p.IsVictim);
    public IEnumerable<DeadlockProcess> Others  => Processes.Where(p => !p.IsVictim);

    /// <summary>"SPID 55 · app_login · dbo.usp_Transfer" for each victim.</summary>
    public string VictimText => Describe(Victims);

    /// <summary>The same, for every process that wasn't a victim.</summary>
    public string OthersText => Describe(Others);

    /// <summary>The objects fought over, each once: "Sales.dbo.Orders (PK_Orders); Sales.dbo.Lines".</summary>
    public string ObjectsText => string.Join("; ", Resources.Select(r => r.ObjectText).Distinct(StringComparer.OrdinalIgnoreCase));

    public string DatabasesText => string.Join(", ", Processes
        .Select(p => p.DatabaseName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>First line of the victim's statement: what failed with error 1205.</summary>
    public string VictimStatement => DeadlockProcess.FirstLine(
        Victims.Select(v => v.Statement ?? v.InputBuffer).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s))) ?? string.Empty;

    /// <summary>Distinct sessions; a parallel query shows up as several processes of one SPID.</summary>
    public int SessionCount => Processes.Select(p => p.SessionId?.ToString() ?? p.Id).Distinct().Count();

    /// <summary>How many deadlocks in the range shown have the same pattern; set by the page.</summary>
    public int TimesSeen { get; init; } = 1;

    private static string Describe(IEnumerable<DeadlockProcess> processes) => string.Join("; ", processes
        // A parallel query's worker processes share the SPID; list the session once.
        .GroupBy(p => p.SessionId?.ToString() ?? p.Id)
        .Select(g => g.First())
        .Select(p => string.Join(" · ", new[] { p.SessionText, p.LoginName, p.CodeText }
            .Where(s => !string.IsNullOrWhiteSpace(s)))));
}

/// <summary>Where a deadlock history was read from.</summary>
public enum DeadlockSource
{
    /// <summary>Nothing could be read; <see cref="DeadlockHistory.Problem"/> says why.</summary>
    None,

    /// <summary>system_health's event files, which reach furthest back.</summary>
    EventFile,

    /// <summary>system_health's ring buffer: only the most recent events, and lost on restart.</summary>
    RingBuffer,
}

/// <summary>The deadlocks system_health still holds, newest first.</summary>
/// <param name="Deadlocks">Newest first, at most <see cref="MaxDeadlocks"/>.</param>
/// <param name="TotalFound">Reports found before the cap, so the page can say some were left out.</param>
/// <param name="Unreadable">Reports whose XML couldn't be read.</param>
/// <param name="Location">The event file pattern read, for the status line.</param>
/// <param name="Problem">Why nothing, or less than the event files, could be read.</param>
public sealed record DeadlockHistory(
    IReadOnlyList<DeadlockReport> Deadlocks,
    DeadlockSource Source,
    int     TotalFound,
    int     Unreadable,
    string? Location,
    string? Problem)
{
    /// <summary>Most reports kept; each holds its full XML for the .xdl.</summary>
    public const int MaxDeadlocks = 1000;

    public static DeadlockHistory Unavailable(string problem) => new([], DeadlockSource.None, 0, 0, null, problem);
}
