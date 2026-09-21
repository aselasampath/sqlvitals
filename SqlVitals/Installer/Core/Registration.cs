using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Microsoft.Win32;

namespace SqlVitals.Installer.Core;

/// <summary>Who the installation belongs to: the current user only, or everyone on the machine.</summary>
public enum InstallScope
{
    User,
    Machine,
}

/// <summary>An installation found in the Windows "Installed apps" list.</summary>
public sealed class ExistingInstallation
{
    public ExistingInstallation(Version version, string installDir, InstallScope scope, bool desktopShortcut)
    {
        Version         = version;
        InstallDir      = installDir;
        Scope           = scope;
        DesktopShortcut = desktopShortcut;
    }

    public Version      Version         { get; }
    public string       InstallDir      { get; }
    public InstallScope Scope           { get; }
    public bool         DesktopShortcut { get; }
}

/// <summary>
/// Reads and writes SqlVitals' entry under ...\CurrentVersion\Uninstall, which is what makes it
/// appear in Settings → Apps with Modify (repair/upgrade) and Uninstall buttons.
/// </summary>
public static class Registration
{
    private const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + ProductInfo.RegistryKeyName;
    private const string DesktopShortcutValue = "SqlVitalsDesktopShortcut";

    public static ExistingInstallation? Detect()
    {
        // Per-user first: that is the default, and a user-level entry is the one this user can change.
        return Read(InstallScope.User) ?? Read(InstallScope.Machine);
    }

    public static ExistingInstallation? Read(InstallScope scope)
    {
        try
        {
            using var baseKey = OpenBase(scope);
            using var key     = baseKey.OpenSubKey(UninstallRoot);
            if (key is null) return null;

            var dir = key.GetValue("InstallLocation") as string;
            if (string.IsNullOrWhiteSpace(dir) ||
                !Version.TryParse(key.GetValue("DisplayVersion") as string, out var version))
                return null;

            var desktop = key.GetValue(DesktopShortcutValue) is int i && i != 0;
            return new ExistingInstallation(version, dir!, scope, desktop);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    public static void Write(InstallScope scope, string installDir, Version version, long estimatedSizeBytes, bool desktopShortcut)
    {
        var setupExe = Path.Combine(installDir, ProductInfo.SetupExeName);
        var appExe   = Path.Combine(installDir, ProductInfo.AppExeName);

        using var baseKey = OpenBase(scope);
        using var key     = baseKey.CreateSubKey(UninstallRoot, writable: true)
                            ?? throw new UnauthorizedAccessException("Could not create the uninstall registry entry.");

        key.SetValue("DisplayName",     ProductInfo.Name);
        key.SetValue("DisplayVersion",  version.ToString());
        key.SetValue("Publisher",       ProductInfo.Publisher);
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon",     appExe);
        key.SetValue("InstallDate",     DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture));
        key.SetValue("UninstallString", $"\"{setupExe}\" /uninstall");
        key.SetValue("ModifyPath",      $"\"{setupExe}\"");
        key.SetValue("EstimatedSize",   (int)Math.Min(int.MaxValue, estimatedSizeBytes / 1024), RegistryValueKind.DWord);
        key.SetValue("NoRepair",        1, RegistryValueKind.DWord);   // Repair is offered inside Setup (Modify)
        key.SetValue(DesktopShortcutValue, desktopShortcut ? 1 : 0, RegistryValueKind.DWord);
    }

    public static void Delete(InstallScope scope)
    {
        using var baseKey = OpenBase(scope);
        baseKey.DeleteSubKeyTree(UninstallRoot, throwOnMissingSubKey: false);
    }

    /// <summary>Copies every value of the entry so a failed install can put it back exactly.</summary>
    public static Dictionary<string, (object Value, RegistryValueKind Kind)>? Snapshot(InstallScope scope)
    {
        using var baseKey = OpenBase(scope);
        using var key     = baseKey.OpenSubKey(UninstallRoot);
        if (key is null) return null;

        var values = new Dictionary<string, (object, RegistryValueKind)>();
        foreach (var name in key.GetValueNames())
            values[name] = (key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)!, key.GetValueKind(name));
        return values;
    }

    public static void Restore(InstallScope scope, Dictionary<string, (object Value, RegistryValueKind Kind)>? snapshot)
    {
        Delete(scope);
        if (snapshot is null) return;

        using var baseKey = OpenBase(scope);
        using var key     = baseKey.CreateSubKey(UninstallRoot, writable: true)!;
        foreach (var pair in snapshot)
            key.SetValue(pair.Key, pair.Value.Value, pair.Value.Kind);
    }

    private static RegistryKey OpenBase(InstallScope scope) =>
        RegistryKey.OpenBaseKey(
            scope == InstallScope.Machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser,
            Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
}
