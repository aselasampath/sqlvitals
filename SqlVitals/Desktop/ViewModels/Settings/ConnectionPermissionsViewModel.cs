using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.ViewModels.Settings;

/// <summary>
/// A connection's Check permissions (#177), opened from its card in Settings → Connections: the
/// same check as the first-run flow's, started as it opens.
/// </summary>
public sealed partial class ConnectionPermissionsViewModel
{
    internal ConnectionPermissionsViewModel(ConnectionSettings connection, PermissionChecker check, IClipboard clipboard)
    {
        ConnectionId = connection.Id;
        Name  = connection.DisplayName;
        Title = $"Permissions of {connection.DisplayName}";
        Line  = $"{connection.Server.Trim()} / {connection.DatabaseLabel} · {connection.AuthenticationLabel}";
        Check = new PermissionCheckViewModel(token => check(connection, token), clipboard, connection.DisplayName);
    }

    /// <summary>"← Connections".</summary>
    public event Action? Closed;

    public Guid ConnectionId { get; }

    public string Name  { get; }
    public string Title { get; }
    public string Line  { get; }

    public PermissionCheckViewModel Check { get; }

    [RelayCommand]
    private void Back()
    {
        Check.Cancel();
        Closed?.Invoke();
    }
}
