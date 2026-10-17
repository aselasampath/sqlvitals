using System.Windows.Controls;
using SqlVitals.Desktop.ViewModels.Settings;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>
/// Settings → Monitoring &amp; history (#131), bound to its <see cref="MonitoringCategoryViewModel"/>
/// (#190). Only view code here: leaving the timeout box saves it, as Enter does.
/// </summary>
internal partial class MonitoringCategory : UserControl
{
    public MonitoringCategory(MonitoringCategoryViewModel model)
    {
        ViewModel   = model;
        DataContext = model;
        InitializeComponent();

        TxtTimeout.LostKeyboardFocus += (_, _) => model.SaveTimeoutCommand.Execute(null);
        Loaded   += (_, _) => model.Activate();
        Unloaded += (_, _) => model.Deactivate();
    }

    public MonitoringCategoryViewModel ViewModel { get; }
}
