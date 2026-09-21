using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Security.Principal;

namespace SqlVitals.Installer.Core;

public static class Elevation
{
    private const int ErrorCancelled = 1223;   // the user said No at the UAC prompt

    public static bool IsElevated
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    /// <summary>Starts another copy of Setup with admin rights. False if the user declined the prompt.</summary>
    public static bool RelaunchElevated(string exePath, params string[] args)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exePath, string.Join(" ", args.Select(Quote)))
            {
                UseShellExecute = true,
                Verb            = "runas",
            });
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    /// <summary>
    /// Opens the installed app as the signed-in user even if Setup is elevated, so SqlVitals never
    /// runs with admin rights it didn't ask for (and reads the user's own saved connections).
    /// </summary>
    public static void LaunchAsCurrentUser(string exePath, string workingDir)
    {
        if (IsElevated)
            Process.Start(new ProcessStartInfo("explorer.exe", Quote(exePath)) { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true, WorkingDirectory = workingDir });
    }

    private static string Quote(string arg) => arg.Contains(" ") ? $"\"{arg}\"" : arg;
}
