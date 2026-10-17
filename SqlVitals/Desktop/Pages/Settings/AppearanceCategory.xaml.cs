using System.Windows.Controls;
using SqlVitals.Desktop.ViewModels.Settings;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>Settings → Appearance (#131), bound to its <see cref="AppearanceCategoryViewModel"/> (#190).</summary>
internal partial class AppearanceCategory : UserControl
{
    public AppearanceCategory(AppearanceCategoryViewModel model)
    {
        ViewModel   = model;
        DataContext = model;
        InitializeComponent();
    }

    public AppearanceCategoryViewModel ViewModel { get; }
}
