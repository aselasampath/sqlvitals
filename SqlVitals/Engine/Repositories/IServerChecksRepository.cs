using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Engine.Repositories;

/// <summary>The health checks read every few minutes rather than every sample (#174): disk, backups, Agent jobs.</summary>
public interface IServerChecksRepository
{
    /// <summary>
    /// Free space on each volume holding database files, the databases past their backup RPO and
    /// the Agent jobs that failed since the last read. Never throws for Azure SQL Database or a
    /// part the login can't read: that part is null, with why.
    /// </summary>
    Task<ServerCheckFigures> GetServerChecksAsync(ServerChecksRequest request, CancellationToken cancellationToken = default);
}
