using System;
using System.Windows;
using System.Windows.Controls;
using SqlVitals.Installer.Core;

namespace SqlVitals.Installer.Views;

public partial class UninstallView : UserControl
{
    private readonly SetupSession _session;

    public UninstallView(SetupSession session)
    {
        InitializeComponent();
        _session = session;
    }

    public event EventHandler? RestartElevatedRequested;

    public bool RemoveUserData => RemoveData.IsChecked == true;

    /// <summary>A per-machine installation can only be removed with admin rights.</summary>
    public bool CanUninstall => _session.Existing is { } e && (e.Scope == InstallScope.User || Elevation.IsElevated);

    public void Load()
    {
        var existing = _session.Existing;
        Intro.Text = existing is null
            ? "SqlVitals doesn't appear to be installed."
            : $"This removes SqlVitals {existing.Version.ToString(3)} from {existing.InstallDir}.";

        DataPath.Text = $"Stored for your Windows account in {ProductInfo.UserDataDir} and {ProductInfo.HistoryDataDir}";
        ElevatePanel.Visibility = existing is not null && !CanUninstall ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Elevate_Click(object sender, RoutedEventArgs e) => RestartElevatedRequested?.Invoke(this, EventArgs.Empty);
}
