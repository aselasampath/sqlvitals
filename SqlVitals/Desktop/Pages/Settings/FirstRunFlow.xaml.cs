using System.ComponentModel;
using System.Windows.Controls;
using SqlVitals.Desktop.ViewModels.Settings;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>
/// First run (#177), over Settings while no connection is saved, bound to its
/// <see cref="FirstRunFlowViewModel"/> (#190): Connect is the connection editor's view, Check the
/// permission check's. Only view code here: which view each step shows.
/// </summary>
internal partial class FirstRunFlow : UserControl
{
    public FirstRunFlow(FirstRunFlowViewModel model)
    {
        ViewModel   = model;
        DataContext = model;
        InitializeComponent();

        Editor = new ConnectionEditor(model.Editor);
        ConnectStep.Content = Editor;
        model.PropertyChanged += OnModelChanged;
    }

    public FirstRunFlowViewModel ViewModel { get; }

    /// <summary>Step 1: the new connection's page.</summary>
    public ConnectionEditor Editor { get; }

    /// <summary>Step 2's check, once a connection is saved.</summary>
    public PermissionCheckView? Check => CheckHost.Content as PermissionCheckView;

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FirstRunFlowViewModel.Check))
            CheckHost.Content = ViewModel.Check is { } check ? new PermissionCheckView(check) : null;
    }
}
