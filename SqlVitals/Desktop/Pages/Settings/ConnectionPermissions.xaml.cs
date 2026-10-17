using System.Windows.Controls;
using SqlVitals.Desktop.ViewModels.Settings;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>
/// A connection's Check permissions (#177), opened from its card in Settings → Connections, bound to
/// its <see cref="ConnectionPermissionsViewModel"/> (#190). Leaving it stops a check still running.
/// </summary>
internal partial class ConnectionPermissions : UserControl
{
    public ConnectionPermissions(ConnectionPermissionsViewModel model)
    {
        ViewModel   = model;
        DataContext = model;
        InitializeComponent();

        Check = new PermissionCheckView(model.Check);
        CheckHost.Content = Check;
        Unloaded += (_, _) => model.Check.Cancel();
    }

    public ConnectionPermissionsViewModel ViewModel { get; }

    public PermissionCheckView Check { get; }
}
