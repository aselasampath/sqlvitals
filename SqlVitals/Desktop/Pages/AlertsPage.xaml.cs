using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows;
using SqlVitals.Desktop.Controls;
using SqlVitals.Desktop.Services;
using SqlVitals.Desktop.ViewModels.Pages;
using SqlVitals.Engine.Monitoring;
using SqlVitals.Engine.Verdicts;
using AlertRow = SqlVitals.Desktop.ViewModels.Pages.AlertsViewModel.AlertRow;

namespace SqlVitals.Desktop.Pages;

/// <summary>
/// Alerts, bound to <see cref="AlertsViewModel"/> (#34, #187): the code-behind fills the details
/// drawer with the alert selected, and turns a click on the Evidence link, a double-click on a row
/// or Enter into the view model's Investigate.
/// </summary>
public partial class AlertsPage : Page, IPageContext, IDetailsSource
{
    // ── The details drawer (#150): the selected alert ─────────────────────────
    private const string SelectHint = "Select an alert to see its figures, its times and what the health dot said.";

    private readonly RowDrawer _drawer = new("Alert details", SelectHint);

    public DrawerContent Details => _drawer.Content;

    public event Action<bool>? DetailsChanged
    {
        add    => _drawer.Changed += value;
        remove => _drawer.Changed -= value;
    }

    internal RowDetailsView? DetailsView => _drawer.View;

    private void ShowAlert(object row, bool open)
    {
        if (row is AlertRow alert)
            _drawer.Show($"{alert.IndicatorName} · {alert.ConnectionName}",
                         RowDetails.Rows(new NamedAlert(alert.Alert, alert.ConnectionName), DateTime.UtcNow), open);
    }

    internal AlertsViewModel ViewModel { get; }

    // Alerts as they start and end, from every monitored connection (#140).
    public PageKind Kind => PageKind.Live;

    /// <param name="folds">Where the verdict's fold is remembered; none in some tests.</param>
    public AlertsPage(AlertsViewModel viewModel, ISectionFolds? folds = null)
    {
        ViewModel   = viewModel;
        DataContext = viewModel;
        InitializeComponent();

        ViewModel.Attach(Shell);
        Shell.Folds = folds;
        DrawerParts.FollowSelection(AlertsGrid, ShowAlert, _drawer.Clear);

        // Investigate (#145): the link in the Evidence column, a double-click on the row, or Enter.
        // The grid handles the mouse itself, so the link needs the clicks it has handled too.
        AlertsGrid.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(AlertsGrid_MouseLeftButtonUp), handledEventsToo: true);
        AlertsGrid.MouseDoubleClick += AlertsGrid_MouseDoubleClick;
        AlertsGrid.PreviewKeyDown   += AlertsGrid_PreviewKeyDown;
    }

    private void AlertsGrid_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is TextBlock { DataContext: AlertRow row } text && CellOf(text)?.Column == InvestigateColumn)
            ViewModel.InvestigateCommand.Execute(row);
    }

    private void AlertsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // Only a row: not the header or the scroll bar.
        if (e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(AlertsGrid, source) is DataGridRow { Item: AlertRow row })
            ViewModel.InvestigateCommand.Execute(row);
    }

    private void AlertsGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || AlertsGrid.SelectedItem is not AlertRow row)
            return;
        e.Handled = true;
        ViewModel.InvestigateCommand.Execute(row);
    }

    private static DataGridCell? CellOf(DependencyObject? element)
    {
        while (element is not null and not DataGridCell)
            element = VisualTreeHelper.GetParent(element);
        return element as DataGridCell;
    }
}
