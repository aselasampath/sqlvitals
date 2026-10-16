using System.Windows.Controls;
using SqlVitals.Desktop.ViewModels.Settings;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>Settings → Collector service (#172), bound to its <see cref="CollectorCategoryViewModel"/> (#190).</summary>
internal partial class CollectorCategory : UserControl
{
    public CollectorCategory(CollectorCategoryViewModel model)
    {
        ViewModel   = model;
        DataContext = model;
        InitializeComponent();

        Loaded   += (_, _) => model.Activate();
        Unloaded += (_, _) => model.Deactivate();
    }

    public CollectorCategoryViewModel ViewModel { get; }
}
