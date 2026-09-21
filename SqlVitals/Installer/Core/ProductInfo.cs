using System;
using System.IO;

namespace SqlVitals.Installer.Core;

/// <summary>Fixed names and locations shared by every part of Setup.</summary>
public static class ProductInfo
{
    public const string Name            = "SqlVitals";
    public const string Publisher       = "SqlVitals";
    public const string AppExeName      = "SqlVitals.Desktop.exe";
    public const string AppProcessName  = "SqlVitals.Desktop";

    /// <summary>Copy of Setup kept in the install folder for Repair and Uninstall from Windows Settings.</summary>
    public const string SetupExeName    = "SqlVitals Setup.exe";

    /// <summary>List of the files Setup installed, so upgrades and uninstall touch nothing else.</summary>
    public const string ManifestFileName = "install-manifest.txt";

    public const string ShortcutFileName = "SqlVitals.lnk";

    /// <summary>Key name under ...\CurrentVersion\Uninstall.</summary>
    public const string RegistryKeyName = "SqlVitals";

    /// <summary>Per-user default, like other modern desktop apps — no admin rights needed.</summary>
    public static string DefaultInstallDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name);

    /// <summary>
    /// Where the app keeps the user's saved connections (see ConnectionSettingsService).
    /// Setup never reads or changes it; only an explicit opt-in on uninstall removes it.
    /// </summary>
    public static string UserDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name);
}
