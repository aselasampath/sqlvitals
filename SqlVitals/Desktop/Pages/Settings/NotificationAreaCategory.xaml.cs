using System.Windows.Controls;
using SqlVitals.Desktop.ViewModels.Settings;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>Settings → Notification area (#131), bound to its <see cref="NotificationAreaCategoryViewModel"/> (#190).</summary>
internal partial class NotificationAreaCategory : UserControl
{
    public NotificationAreaCategory(NotificationAreaCategoryViewModel model)
    {
        ViewModel   = model;
        DataContext = model;
        InitializeComponent();
    }

    public NotificationAreaCategoryViewModel ViewModel { get; }
}
