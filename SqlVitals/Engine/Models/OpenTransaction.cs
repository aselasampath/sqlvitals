namespace SqlVitals.Engine.Models;

/// <summary>
/// The oldest open user transaction on the server that holds locks or log (#252): an explicit one,
/// or one that has written to the log. Read with every monitoring sample.
/// </summary>
/// <param name="Seconds">How long it has been open, on the server's clock.</param>
/// <param name="Status">Its session's status: "sleeping" with no request running, "running", "suspended"…</param>
/// <param name="Database">The database it has written most log in, else its session's.</param>
/// <param name="LogReuseWait">That database's log_reuse_wait_desc, e.g. "ACTIVE_TRANSACTION", when it has written to its log; null otherwise.</param>
public sealed record OpenTransaction(
    double  Seconds,
    int     SessionId,
    string? Login,
    string? Host,
    string? Program,
    string? Status,
    string? Database,
    string? LogReuseWait);
