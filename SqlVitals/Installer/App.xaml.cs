using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using SqlVitals.Installer.Core;

namespace SqlVitals.Installer;

public partial class App : Application
{
    public const  string UninstallArg  = "/uninstall";
    public const  string InstallDirArg = "/dir=";
    private const string RelaunchedArg = "/relaunched";

    private string _exePath = string.Empty;
    private bool   _isTempCopy;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        _exePath = Assembly.GetExecutingAssembly().Location;
        var args       = e.Args;
        var uninstall  = HasArg(args, UninstallArg);
        var relaunched = HasArg(args, RelaunchedArg);
        _isTempCopy    = relaunched && IsOurTempCopy(_exePath);

        SetupLog.Info($"SqlVitals Setup {Assembly.GetExecutingAssembly().GetName().Version} started " +
                      $"(args: {string.Join(" ", args)}; elevated: {Elevation.IsElevated})");

        var existing = Registration.Detect();
        if (existing is not null)
            SetupLog.Info($"Existing installation: {existing.Version} at {existing.InstallDir} ({existing.Scope})");

        // Windows Settings starts the copy of Setup inside the install folder. That copy is one of
        // the files being replaced or removed, so hand over to a temporary copy first.
        if (!relaunched && existing is not null && FileSystemHelpers.IsUnder(_exePath, existing.InstallDir))
        {
            if (RelaunchFromTemp(args))
            {
                Shutdown();
                return;
            }
        }

        Payload? payload = null;
        string?  payloadError = null;
        try
        {
            payload = Payload.OpenEmbedded();
            if (payload is null)
                payloadError = "this Setup was built without the application files";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            payloadError = ex.Message;
            SetupLog.Error("Could not open the embedded package", ex);
        }

        var session = new SetupSession(_exePath, args.Where(a => !HasArg(new[] { a }, RelaunchedArg)).ToArray(),
                                       payload, payloadError, existing, uninstall && existing is not null);

        // Folder chosen before an elevated restart (fresh installs only; others keep their folder).
        var dirArg = args.FirstOrDefault(a => a.StartsWith(InstallDirArg, StringComparison.OrdinalIgnoreCase));
        if (dirArg is not null && existing is null &&
            LocationValidator.CheckPath(dirArg.Substring(InstallDirArg.Length), out var chosenDir) is null)
            session.InstallDir = chosenDir;

        MainWindow = new MainWindow(session);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SetupLog.Info($"Setup exited ({e.ApplicationExitCode}).");

        // Remove the temporary copy once this process has gone.
        if (_isTempCopy)
        {
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", $"/d /c timeout /t 3 /nobreak > nul & del /f /q \"{_exePath}\"")
                {
                    CreateNoWindow  = true,
                    UseShellExecute = false,
                    WindowStyle     = ProcessWindowStyle.Hidden,
                });
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                SetupLog.Warn($"Could not schedule removal of {_exePath}: {ex.Message}");
            }
        }

        base.OnExit(e);
    }

    /// <summary>Restarts Setup with admin rights, keeping the same arguments. Returns false if the user declined.</summary>
    public bool RestartElevated(string[] args)
    {
        var passArgs = _isTempCopy ? args.Concat(new[] { RelaunchedArg }).ToArray() : args;
        if (!Elevation.RelaunchElevated(_exePath, passArgs))
            return false;

        _isTempCopy = false;   // the elevated copy is now running this exe; it cleans up after itself
        Shutdown();
        return true;
    }

    private bool RelaunchFromTemp(string[] args)
    {
        try
        {
            var temp = Path.Combine(Path.GetTempPath(), $"SqlVitals-Setup-{Guid.NewGuid():N}.exe");
            File.Copy(_exePath, temp);
            Process.Start(new ProcessStartInfo(temp, string.Join(" ", args.Concat(new[] { RelaunchedArg })))
            {
                UseShellExecute = false,
            });
            SetupLog.Info($"Relaunched from {temp}");
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            // Carry on from here: every step still works, only Setup's own copy may stay behind.
            SetupLog.Warn($"Could not relaunch from a temporary copy: {ex.Message}");
            return false;
        }
    }

    private static bool IsOurTempCopy(string exePath) =>
        FileSystemHelpers.IsUnder(exePath, Path.GetTempPath()) &&
        Path.GetFileName(exePath).StartsWith("SqlVitals-Setup-", StringComparison.OrdinalIgnoreCase);

    private static bool HasArg(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase) ||
                      string.Equals(a, "-" + name.TrimStart('/'), StringComparison.OrdinalIgnoreCase));

    private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        SetupLog.Error("Unhandled error", e.Exception);
        MessageBox.Show(
            $"Setup ran into an unexpected problem:\n\n{e.Exception.Message}\n\nDetails were saved to:\n{SetupLog.Path}",
            "SqlVitals Setup", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
