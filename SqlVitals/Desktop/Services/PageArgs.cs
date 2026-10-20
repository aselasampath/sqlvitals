using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// What the window opens a page with (#183): the route, and what belongs to the connection open
/// now. The page factory hands it to the page's registration with the app's services, which
/// come from the container since #166.
/// </summary>
/// <param name="Route">The route being shown: its tag, and the server or connection it is about.</param>
/// <param name="Repo">The active connection's repository.</param>
public sealed record PageArgs(Route Route, IWaitStatsRepository Repo)
{
    /// <summary>The connection open now; null when none is.</summary>
    public Guid? ConnectionId { get; init; }

    /// <summary>The open connection's name, as the selector shows it.</summary>
    public string? ConnectionName { get; init; }

    /// <summary>The open server's clock from UTC, when it is being sampled.</summary>
    public TimeSpan? ServerOffset { get; init; }

    /// <summary>The open server is Azure SQL Database, as the shell's check on switching found (#234); false when not known.</summary>
    public bool IsAzureSqlDatabase { get; init; }

    /// <summary>Settings tells the app its saved connections changed; true opens the active server's Overview.</summary>
    public Func<bool, Task>? ApplyConnections { get; init; }

    /// <summary>A message at the top of Settings: "Switched to UAT.", a password prompt.</summary>
    public string? SettingsMessage { get; init; }
}
