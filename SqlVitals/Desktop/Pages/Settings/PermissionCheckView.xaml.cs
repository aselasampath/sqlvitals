using System.Windows.Controls;
using SqlVitals.Desktop.ViewModels;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>
/// The permission check's results and GRANT script (#177), in the first-run flow's Check step and
/// on a connection's Check permissions page. Everything is bound to its view model.
/// </summary>
internal partial class PermissionCheckView : UserControl
{
    public PermissionCheckView(PermissionCheckViewModel model)
    {
        Model = model;
        DataContext = model;
        InitializeComponent();
    }

    public PermissionCheckViewModel Model { get; }
}
