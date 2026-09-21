using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using SqlVitals.Installer.Core;
using WinForms = System.Windows.Forms;

namespace SqlVitals.Installer.Views;

public partial class OptionsView : UserControl
{
    private readonly SetupSession _session;

    public OptionsView(SetupSession session)
    {
        InitializeComponent();
        _session = session;
    }

    public event EventHandler? RestartElevatedRequested;

    /// <summary>The folder as typed, normalised when valid.</summary>
    public string EnteredLocation =>
        LocationValidator.CheckPath(DirBox.Text, out var fullPath) is null ? fullPath : DirBox.Text.Trim();

    public void Load()
    {
        DirBox.Text = _session.InstallDir;
        DesktopShortcut.IsChecked = _session.CreateDesktopShortcut;

        var canChoose = _session.CanChooseLocation;
        DirBox.IsReadOnly     = !canChoose;
        BtnBrowse.Visibility  = canChoose ? Visibility.Visible : Visibility.Collapsed;
        BtnDefault.Visibility = canChoose ? Visibility.Visible : Visibility.Collapsed;
        DirHint.Text = canChoose
            ? "The default is in your user profile, so it doesn't need administrator rights."
            : $"{_session.ActionVerb} keeps SqlVitals in the folder it's already installed in.";

        HideError();
        UpdateSpace();
    }

    /// <summary>Validates the choices and stores them on the session. False (with the problem shown) if they can't be used.</summary>
    public bool TryApply()
    {
        HideError();

        if (_session.CanChooseLocation)
        {
            var error = LocationValidator.CheckPath(DirBox.Text, out var fullPath);
            if (error is not null)
            {
                ShowError(error);
                return false;
            }

            if (LocationValidator.IsOccupiedByOtherFiles(fullPath))
            {
                ShowError($"This folder already contains other files. Choose an empty or new folder, for example {Path.Combine(fullPath, ProductInfo.Name)}.");
                return false;
            }

            if (!FileSystemHelpers.CanWriteTo(fullPath))
            {
                ShowError(Elevation.IsElevated
                    ? "You don't have permission to install to this folder. Choose another folder."
                    : "You don't have permission to install to this folder. Choose a folder in your user profile (like the default), or restart Setup as administrator.",
                    offerElevation: !Elevation.IsElevated);
                return false;
            }

            var free = SystemRequirements.GetFreeSpace(fullPath);
            if (free is not null && free.Value < _session.RequiredBytes)
            {
                ShowError($"There isn't enough free space on {Path.GetPathRoot(fullPath)}: SqlVitals needs {FileSystemHelpers.FormatSize(_session.RequiredBytes)} " +
                          $"and {FileSystemHelpers.FormatSize(free.Value)} is free. Free up space or choose another drive.");
                return false;
            }

            _session.InstallDir = fullPath;
        }

        _session.CreateDesktopShortcut = DesktopShortcut.IsChecked == true;
        return true;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description         = "Choose where to install SqlVitals. A \"SqlVitals\" folder will be created inside the folder you pick.",
            ShowNewFolderButton = true,
            SelectedPath        = FileSystemHelpers.NearestExistingDirectory(DirBox.Text) ?? string.Empty,
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
            DirBox.Text = LocationValidator.WithProductFolder(dialog.SelectedPath);
    }

    private void Default_Click(object sender, RoutedEventArgs e) => DirBox.Text = ProductInfo.DefaultInstallDir;

    private void Elevate_Click(object sender, RoutedEventArgs e) => RestartElevatedRequested?.Invoke(this, EventArgs.Empty);

    private void DirBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        HideError();
        UpdateSpace();
    }

    private void UpdateSpace()
    {
        var required = FileSystemHelpers.FormatSize(_session.RequiredBytes);
        if (LocationValidator.CheckPath(DirBox.Text, out var fullPath) is null &&
            SystemRequirements.GetFreeSpace(fullPath) is { } free)
            SpaceText.Text = $"Space required: {required}  ·  Available on {Path.GetPathRoot(fullPath)}: {FileSystemHelpers.FormatSize(free)}";
        else
            SpaceText.Text = $"Space required: {required}";
    }

    private void ShowError(string message, bool offerElevation = false)
    {
        ErrorText.Text        = message;
        BtnElevate.Visibility = offerElevation ? Visibility.Visible : Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void HideError() => ErrorPanel.Visibility = Visibility.Collapsed;
}
