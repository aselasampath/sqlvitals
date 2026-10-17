using System.Windows;
using SqlVitals.Desktop.ViewModels.Windows;

namespace SqlVitals.Desktop.Windows;

/// <summary>
/// An execution plan, drawn, with its XML (<see cref="QueryPlanViewModel"/>). The diagram is the
/// window's: it draws the plan, and renders it to a PNG when the view model exports it.
/// </summary>
public partial class QueryExecutionPlanWindow : Window
{
    public QueryExecutionPlanWindow(QueryPlanViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();

        PlanView.Show(viewModel.Plan, QueryPlanViewModel.NoPlanMessage);
        viewModel.HasDiagram     = PlanView.HasDiagram;
        viewModel.RenderDiagram  = path =>
        {
            // Laid out first: the Graphical tab has only just been selected.
            PlanView.UpdateLayout();
            PlanView.ExportPng(path);
        };
        viewModel.CloseRequested += Close;
    }
}
