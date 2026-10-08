using SqlVitals.Engine.Models;

namespace SqlVitals.Engine.Repositories;

/// <summary>Sessions: the process map, a session's plan, and the applications connected (#47).</summary>
public interface ISessionRepository
{
    // ── Process map ─────────────────────────────────────────────────
    Task<IEnumerable<ProcessNode>> GetProcessesAsync(CancellationToken cancellationToken = default);

    /// <summary>The plan a session is running, or its last statement's cached plan (#147).</summary>
    Task<SessionPlan> GetSessionPlanAsync(int sessionId, CancellationToken cancellationToken = default);

    // ── Application connections ──────────────────────────────────────
    Task<IEnumerable<ApplicationConnection>> GetApplicationConnectionsAsync(CancellationToken cancellationToken = default);
}
