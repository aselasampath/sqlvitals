using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using SqlVitals.Installer.Core;

namespace SqlVitals.Installer.Views;

public partial class FinishView : UserControl
{
    private readonly SetupSession _session;
    private string? _appExe;

    public FinishView(SetupSession session)
    {
        InitializeComponent();
        _session = session;
    }

    public void Show(InstallOutcome outcome, InstallContext context)
    {
        NextSteps.Children.Clear();
        _appExe = null;
        var uninstall = context.Mode == InstallMode.Uninstall;

        if (outcome == InstallOutcome.Succeeded && !uninstall)
            ShowInstalled(context);
        else if (outcome == InstallOutcome.Succeeded)
            ShowUninstalled(context);
        else if (uninstall)
            ShowProblem("Uninstall stopped",
                "SqlVitals may be partly removed. Run Setup again (or use Settings → Apps) and choose Uninstall to finish removing it.");
        else if (outcome == InstallOutcome.RolledBack)
            ShowProblem("Installation cancelled",
                _session.Existing is null
                    ? "Setup undid its changes — nothing was installed. You can run Setup again whenever you're ready."
                    : $"Setup undid its changes. Your previous SqlVitals {_session.Existing.Version.ToString(3)} installation is unchanged and still works.",
                warning: false);
        else
            ShowProblem("Setup couldn't undo every change",
                $"Some changes couldn't be reversed. Run Setup again and choose Repair (or Install) to fix the installation. " +
                $"Details are in the setup log: {SetupLog.Path}");
    }

    private void ShowInstalled(InstallContext context)
    {
        SetBadge("✓", "SuccessSoft", "Success");
        var version = context.Payload!.Manifest.Version.ToString(3);
        Title.Text = context.Mode switch
        {
            InstallMode.Upgrade   => "SqlVitals is upgraded",
            InstallMode.Repair    => "SqlVitals is repaired",
            InstallMode.Downgrade => "SqlVitals is replaced",
            _                     => "SqlVitals is installed",
        };
        Summary.Text = $"SqlVitals {version} is ready in {context.InstallDir}.";

        _appExe = context.AppExePath;
        BtnLaunch.Visibility     = File.Exists(_appExe) ? Visibility.Visible : Visibility.Collapsed;
        NextStepsCard.Visibility = Visibility.Visible;
        NextStepsTitle.Text      = "Next steps";

        if (context.Mode == InstallMode.Install)
        {
            AddStep(new Run("Connect to your database. "), Bold("Launch SqlVitals"), new Run(", open "), Bold("⚙️ Settings"),
                    new Run(" in the sidebar, enter your server name and sign-in details, then click "), Bold("Save & Connect"), new Run("."));
            AddStep(new Run("Check the SQL login's permissions. SqlVitals needs "), Bold("VIEW SERVER STATE"),
                    new Run(" (SQL Server or Managed Instance) or "), Bold("VIEW DATABASE STATE"),
                    new Run(" (Azure SQL Database). Your database administrator can grant it."));
            AddStep(new Run("Optional: for per-call stored procedure tracing, the login also needs "), Bold("ALTER ANY EVENT SESSION"),
                    new Run(". Without it SP Trace still works in a lighter summary mode."));
        }
        else
        {
            AddStep(new Run("Your saved connections were kept — SqlVitals opens with them as before."));
        }

        foreach (var file in context.PreservedConfigFiles)
            AddStep(new Run($"You had edited {file}, so your version was kept. The new default settings are saved beside it as "),
                    Bold(file + ".new"), new Run(" if you want to compare them."));

        AddStep(new Run(context.CreateDesktopShortcut
                    ? "Find SqlVitals later in the Start menu or on your desktop. "
                    : "Find SqlVitals later in the Start menu. "),
                new Run("To repair or remove it, use "), Bold("Settings → Apps → Installed apps"), new Run("."));
    }

    private void ShowUninstalled(InstallContext context)
    {
        SetBadge("✓", "SuccessSoft", "Success");
        Title.Text   = "SqlVitals was removed";
        Summary.Text = "SqlVitals has been uninstalled from this PC.";
        BtnLaunch.Visibility     = Visibility.Collapsed;
        NextStepsCard.Visibility = Visibility.Visible;
        NextStepsTitle.Text      = "Good to know";

        AddStep(new Run(context.RemoveUserData
            ? "Your saved connections and settings were deleted."
            : $"Your saved connections were kept in {ProductInfo.UserDataDir}. If you install SqlVitals again, they'll be there."));

        foreach (var dir in context.LeftBehind)
            AddStep(new Run($"Some files that Setup didn't install were left in {dir}. Delete them yourself if you no longer need them."));
    }

    private void ShowProblem(string title, string summary, bool warning = true)
    {
        SetBadge(warning ? "!" : "–", warning ? "WarningSoft" : "BgDeep", warning ? "Warning" : "TextMuted");
        Title.Text   = title;
        Summary.Text = summary;
        BtnLaunch.Visibility     = Visibility.Collapsed;
        NextStepsCard.Visibility = Visibility.Collapsed;
    }

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        if (_appExe is null) return;
        try
        {
            Elevation.LaunchAsCurrentUser(_appExe, Path.GetDirectoryName(_appExe)!);
            SetupLog.Info("Launched SqlVitals.");
            BtnLaunch.Content   = "SqlVitals is starting…";
            BtnLaunch.IsEnabled = false;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            SetupLog.Error("Could not launch SqlVitals", ex);
            MessageBox.Show(Window.GetWindow(this)!,
                $"SqlVitals couldn't be started: {ex.Message}\n\nYou can open it from the Start menu instead.",
                "SqlVitals Setup", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(SetupLog.Path))
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{SetupLog.Path}\"") { UseShellExecute = true });
    }

    private void AddStep(params Inline[] inlines)
    {
        var number = NextSteps.Children.Count + 1;
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());

        var num = new TextBlock { Text = $"{number}.", Foreground = (Brush)FindResource("Accent"), FontWeight = FontWeights.SemiBold };
        var text = new TextBlock();
        text.Inlines.AddRange(inlines);
        Grid.SetColumn(text, 1);

        grid.Children.Add(num);
        grid.Children.Add(text);
        NextSteps.Children.Add(grid);
    }

    private static Run Bold(string text) => new(text) { FontWeight = FontWeights.SemiBold };

    private void SetBadge(string icon, string background, string foreground)
    {
        BadgeIcon.Text       = icon;
        Badge.Background     = (Brush)FindResource(background);
        BadgeIcon.Foreground = (Brush)FindResource(foreground);
    }
}
