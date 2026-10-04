namespace SqlVitals.Engine.Models;

/// <summary>A session's plan: what it is running, or its last statement's cached plan (#147).</summary>
/// <param name="PlanXml">The showplan XML; null when none is cached any more.</param>
/// <param name="IsRunning">The plan of a request running now, rather than of its last statement.</param>
public sealed record SessionPlan(string? PlanXml, bool IsRunning);
