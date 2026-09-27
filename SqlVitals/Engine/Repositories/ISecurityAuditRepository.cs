using SqlVitals.Engine.Security;

namespace SqlVitals.Engine.Repositories;

/// <summary>A quick audit of the server's riskiest security settings (#43).</summary>
public interface ISecurityAuditRepository
{
    /// <summary>
    /// Reads the sa login, the sysadmin members, each database's TRUSTWORTHY setting and each
    /// database's orphaned users, and runs <see cref="SecurityAudit"/> on them. Never throws for a
    /// login without VIEW ANY DEFINITION, or for a database that can't be read: the checks say so instead.
    /// </summary>
    Task<SecurityFindingList> GetSecurityAuditAsync();
}
