using System.Windows;
using System.Windows.Controls;
using SqlVitals.Installer.Core;

namespace SqlVitals.Installer.Views;

public partial class ReviewView : UserControl
{
    private readonly SetupSession _session;

    public ReviewView(SetupSession session)
    {
        InitializeComponent();
        _session = session;
    }

    public void Load()
    {
        var s        = _session;
        var package  = s.PackageVersionText;
        var existing = s.Existing?.Version.ToString(3);

        Title.Text = s.Mode switch
        {
            InstallMode.Upgrade   => "Ready to upgrade",
            InstallMode.Repair    => "Ready to repair",
            InstallMode.Downgrade => "Ready to replace",
            _                     => "Ready to install",
        };
        Intro.Text = $"Check the details below, then click {s.ActionVerb}. You can go Back to change anything.";

        Summary.Children.Clear();
        Summary.RowDefinitions.Clear();

        AddRow("Action", s.Mode switch
        {
            InstallMode.Upgrade   => $"Upgrade SqlVitals {existing} to {package}",
            InstallMode.Repair    => $"Repair SqlVitals {package} (reinstall all program files)",
            InstallMode.Downgrade => $"Replace SqlVitals {existing} with {package}",
            _                     => $"Install SqlVitals {package}",
        });
        AddRow("Location",      s.InstallDir);
        AddRow("Installed for", s.Scope == InstallScope.Machine ? "All users of this PC" : "Just you");
        AddRow("Shortcuts",     s.CreateDesktopShortcut ? "Start menu and desktop" : "Start menu");
        AddRow("Disk space",    $"About {FileSystemHelpers.FormatSize(s.RequiredBytes)} during setup");
        AddRow("Source",        "Files included in this Setup — nothing is downloaded. Each file is checked against its SHA-256 fingerprint before it's installed.");

        DataNote.Text = s.Existing is null
            ? "Setup only writes to the folder above, the Start menu (and desktop, if chosen) and the Windows app list. If anything goes wrong, you can retry or cancel, and cancelling removes everything Setup added."
            : $"Your saved connections ({ProductInfo.UserDataDir}) are not touched. If you edited appsettings.json, your version is kept. " +
              "If anything goes wrong, cancelling puts your current installation back exactly as it was.";
    }

    private void AddRow(string label, string value)
    {
        var row = Summary.RowDefinitions.Count;
        Summary.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var l = new TextBlock { Text = label, Style = (Style)FindResource("Muted"), Margin = new Thickness(0, 0, 12, 10) };
        var v = new TextBlock { Text = value, Margin = new Thickness(0, 0, 0, 10) };
        Grid.SetRow(l, row);
        Grid.SetRow(v, row);
        Grid.SetColumn(v, 1);
        Summary.Children.Add(l);
        Summary.Children.Add(v);
    }
}
