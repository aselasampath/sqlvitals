using SqlVitals.Engine.Permissions;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Signs in to a saved connection and reads which permissions its login holds (#177): the
/// first-run flow's Check step and a card's Check permissions. Throws when it can't sign in.
/// </summary>
public delegate Task<PermissionProbe> PermissionChecker(ConnectionSettings connection, CancellationToken cancellationToken);

/// <summary>The app's <see cref="PermissionChecker"/>: a repository of its own per check, so nothing is cached.</summary>
public static class PermissionCheckers
{
    /// <summary>Through the app's repositories, with the query time limit in Settings → Monitoring &amp; history.</summary>
    public static PermissionChecker From(IRepositoryFactory repositories, ConnectionSettingsService settings) =>
        (connection, cancellationToken) =>
            repositories.Create(repositories.TargetFor(connection, settings.Load().CommandTimeoutSeconds))
                        .CheckPermissionsAsync(cancellationToken);
}
