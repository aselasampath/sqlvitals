using System.Windows.Controls;
using SqlVitals.Desktop.ViewModels.Settings;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>
/// Settings → Activity log &amp; diagnostics (#131), bound to its <see cref="ActivityCategoryViewModel"/>
/// (#190). The activity log is a control of its own (#115), handed what to read here.
/// </summary>
internal partial class ActivityCategory : UserControl
{
    public ActivityCategory(ActivityCategoryViewModel model)
    {
        ViewModel   = model;
        DataContext = model;
        InitializeComponent();

        ActivityLogView.Initialize(model.HistoryReader, model.Connections);
        model.Reread += () => _ = ActivityLogView.RefreshQuietlyAsync();
    }

    public ActivityCategoryViewModel ViewModel { get; }
}
