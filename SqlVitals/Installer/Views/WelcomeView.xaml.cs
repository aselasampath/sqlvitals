using System.Windows;
using System.Windows.Controls;
using SqlVitals.Installer.Core;

namespace SqlVitals.Installer.Views;

public partial class WelcomeView : UserControl
{
    private readonly SetupSession _session;
    private readonly InstallMode  _primaryMode;

    public WelcomeView(SetupSession session)
    {
        InitializeComponent();
        _session     = session;
        _primaryMode = session.Mode == InstallMode.Uninstall
            ? InstallPlanner.DefaultMode(session.Existing?.Version, session.PackageVersion ?? new System.Version(0, 0))
            : session.Mode;

        Intro.Text = $"This will install SqlVitals {session.PackageVersionText}, a desktop app for monitoring the " +
                     "performance of SQL Server and Azure SQL databases.";

        if (session.Existing is { } existing)
        {
            FreshPanel.Visibility    = Visibility.Collapsed;
            ExistingPanel.Visibility = Visibility.Visible;
            Intro.Text = $"This is Setup for SqlVitals {session.PackageVersionText}.";

            ExistingTitle.Text    = $"SqlVitals {existing.Version.ToString(3)} is already installed";
            ExistingLocation.Text = $"Location: {existing.InstallDir}";

            var package = session.PackageVersionText;
            var installed = existing.Version.ToString(3);
            (OptPrimaryTitle.Text, OptPrimaryDetail.Text) = _primaryMode switch
            {
                InstallMode.Upgrade   => ($"Upgrade to {package} (recommended)",
                                          $"Replaces version {installed} with {package} in the same folder."),
                InstallMode.Downgrade => ($"Replace with older version {package}",
                                          $"Installs {package} over the newer {installed}. Only do this if you need to go back to an earlier release."),
                _                     => ($"Repair SqlVitals {package}",
                                          "Reinstalls every program file, fixing any that are missing or damaged."),
            };

            // Without a package only uninstall is possible.
            if (session.Payload is null)
            {
                OptPrimary.IsEnabled = false;
                OptPrimaryDetail.Text = "Not available: this copy of Setup doesn't contain the application files.";
                OptUninstall.IsChecked = true;
            }
            else if (session.Mode == InstallMode.Uninstall)
            {
                OptUninstall.IsChecked = true;
            }
        }
    }

    public InstallMode SelectedMode =>
        _session.Existing is not null && OptUninstall.IsChecked == true ? InstallMode.Uninstall : _primaryMode;

    /// <summary>
    /// Always true: even a Setup without its files goes on to the system check, which explains the
    /// problem and how to fix it rather than leaving a disabled button.
    /// </summary>
    public bool CanContinue => true;
}
