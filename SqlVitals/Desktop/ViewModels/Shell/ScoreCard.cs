using SqlVitals.Engine.Monitoring;

namespace SqlVitals.Desktop.ViewModels.Shell;

/// <summary>
/// The open server's health score breakdown beside the sidebar (#36), kept current as samples arrive.
/// </summary>
/// <param name="Name">The connection's name.</param>
/// <param name="Score">Its latest score; null while it isn't monitored, or before the first sample.</param>
/// <param name="NoScore">Why there is no score, or the session's health in words.</param>
/// <param name="Paused">Its monitoring is paused: the score is the last one taken.</param>
public sealed record ScoreCard(string Name, HealthScore? Score, string NoScore, bool Paused);
