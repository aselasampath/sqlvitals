using SqlVitals.Engine.Deadlocks;

namespace SqlVitals.Engine.Repositories;

/// <summary>Recent deadlocks from the built-in system_health Extended Events session (#38).</summary>
public interface IDeadlockRepository
{
    /// <summary>
    /// The <c>xml_deadlock_report</c> events system_health still holds, newest first: from its
    /// event files, or its ring buffer when the files can't be read. Never throws for a missing
    /// permission, a stopped session or Azure SQL Database: the history then carries the reason.
    /// </summary>
    Task<DeadlockHistory> GetDeadlockHistoryAsync();
}
