using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Deadlocks;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Deadlocks, bound to <see cref="DeadlocksViewModel"/> (#38, #187): the code-behind opens a
/// pattern's newest deadlock on a double-click, and scrolls the deadlock a pattern opens into view.
/// </summary>
public partial class DeadlocksPage : Page, IPageContext
{
    // Read once, and again on Refresh (#140).
    public PageKind Kind => PageKind.Snapshot;

    internal DeadlocksViewModel ViewModel { get; }

    /// <param name="folds">Where the verdict's and the source's folds are remembered; none in some tests.</param>
    public DeadlocksPage(DeadlocksViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        ViewModel.ShowRow += ShowDeadlock;
    }

    private void PatternsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PatternsGrid.SelectedItem is DeadlockPattern pattern)
            ViewModel.OpenNewestCommand.Execute(pattern);
    }

    // A pattern's newest deadlock, past a search that hides it and in view once its tab has laid the grid out.
    private void ShowDeadlock(DeadlockReport report)
    {
        if (!DeadlocksGrid.Items.Contains(report))
            DeadlocksFilter.Clear();
        DeadlocksGrid.SelectedItem = report;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (DeadlocksGrid.SelectedItem is not null)
                DeadlocksGrid.ScrollIntoView(DeadlocksGrid.SelectedItem);
        });
    }
}
