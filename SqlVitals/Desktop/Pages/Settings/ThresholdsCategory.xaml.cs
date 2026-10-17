using System.Windows.Controls;
using SqlVitals.Desktop.ViewModels.Settings;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>Settings → Health thresholds (#131), bound to its <see cref="ThresholdsCategoryViewModel"/> (#190).</summary>
internal partial class ThresholdsCategory : UserControl
{
    public ThresholdsCategory(ThresholdsCategoryViewModel model)
    {
        ViewModel   = model;
        DataContext = model;
        InitializeComponent();
    }

    public ThresholdsCategoryViewModel ViewModel { get; }
}
