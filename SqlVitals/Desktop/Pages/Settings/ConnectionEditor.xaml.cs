using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SqlVitals.Desktop.ViewModels.Settings;

namespace SqlVitals.Desktop.Pages.Settings;

/// <summary>
/// A connection's page in Settings → Connections (#131), or a new connection's, bound to its
/// <see cref="ConnectionEditorViewModel"/> (#190). Only view code here: the password box, which
/// can't be bound, the ⋯ menu, and following the settings while it is on screen.
/// </summary>
internal partial class ConnectionEditor : UserControl
{
    // Set while the password box is filled from the view model, so that isn't typing.
    private bool _fillingPassword;

    public ConnectionEditor(ConnectionEditorViewModel model)
    {
        ViewModel   = model;
        DataContext = model;
        InitializeComponent();

        ShowPassword();
        TxtPassword.PasswordChanged += (_, _) =>
        {
            if (!_fillingPassword)
                ViewModel.Password = TxtPassword.Password;
        };
        ViewModel.PropertyChanged += OnModelChanged;
        ViewModel.MenuRequested   += OpenMenu;
        ViewModel.FocusRequested  += Focus;

        Loaded   += (_, _) => ViewModel.Activate();
        Unloaded += (_, _) => ViewModel.Deactivate();
    }

    public ConnectionEditorViewModel ViewModel { get; }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConnectionEditorViewModel.Password))
            ShowPassword();
    }

    // Loaded, discarded or saved: the box shows the view model's password.
    private void ShowPassword()
    {
        if (TxtPassword.Password == ViewModel.Password)
            return;
        _fillingPassword = true;
        try { TxtPassword.Password = ViewModel.Password; }
        finally { _fillingPassword = false; }
    }

    // Once its tab is on screen.
    private void Focus(string field) => Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () =>
    {
        if (field == nameof(ConnectionEditorViewModel.Name))
            TxtConnectionName.Focus();
    });

    private void OpenMenu()
    {
        BtnMore.ContextMenu.DataContext     = ViewModel;
        BtnMore.ContextMenu.PlacementTarget = BtnMore;
        BtnMore.ContextMenu.Placement       = PlacementMode.Bottom;
        BtnMore.ContextMenu.IsOpen          = true;
    }
}
