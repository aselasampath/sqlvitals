using System.Windows;
using SqlVitals.Desktop.ViewModels.Windows;

namespace SqlVitals.Desktop.Windows;

/// <summary>Shows a generated T-SQL script for review, with copy and save (<see cref="SqlScriptViewModel"/>). Never executes it.</summary>
public partial class SqlScriptWindow : Window
{
    public SqlScriptWindow(SqlScriptViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
        viewModel.CloseRequested += Close;
    }
}
