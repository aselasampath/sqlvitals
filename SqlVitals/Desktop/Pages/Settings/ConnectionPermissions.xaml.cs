using System.Windows.Controls;
using System.Windows.Input;
using SqlVitals.Desktop.Helpers;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>
/// A connection's Check permissions (#177), opened from its card in Settings → Connections: the
/// same check as the first-run flow's, started as it opens.
/// </summary>
internal partial class ConnectionPermissions : UserControl
{
    public ConnectionPermissions(ConnectionSettings connection, PermissionChecker check, IClipboard clipboard)
    {
        Check = new PermissionCheckView(new PermissionCheckViewModel(token => check(connection, token), clipboard, connection.DisplayName));
        // Bound with ElementName before InitializeComponent: it doesn't change.
        BackCommand = new RelayCommand(() =>
        {
            Check.Model.Cancel();
            Closed?.Invoke();
        });
        InitializeComponent();

        ConnectionId  = connection.Id;
        RunName.Text  = connection.DisplayName;
        TxtTitle.Text = $"Permissions of {connection.DisplayName}";
        TxtLine.Text  = $"{connection.Server.Trim()} / {connection.DatabaseLabel} · {connection.AuthenticationLabel}";
        CheckHost.Content = Check;
        Unloaded += (_, _) => Check.Model.Cancel();
    }

    /// <summary>"← Connections".</summary>
    public event Action? Closed;

    public ICommand BackCommand { get; }

    public Guid ConnectionId { get; }

    public PermissionCheckView Check { get; }
}
