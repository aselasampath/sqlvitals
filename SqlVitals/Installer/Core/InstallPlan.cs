using System;
using System.Collections.Generic;
using System.IO;

namespace SqlVitals.Installer.Core;

public enum InstallMode
{
    /// <summary>Nothing installed yet.</summary>
    Install,
    /// <summary>An older version is installed.</summary>
    Upgrade,
    /// <summary>The same version is installed: reinstall every file, keep settings.</summary>
    Repair,
    /// <summary>A newer version is installed and the user chose to replace it with this one.</summary>
    Downgrade,
    Uninstall,
}

public static class InstallPlanner
{
    /// <summary>The natural install action for this package given what is already installed.</summary>
    public static InstallMode DefaultMode(Version? installed, Version package)
    {
        if (installed is null) return InstallMode.Install;
        var cmp = Normalize(installed).CompareTo(Normalize(package));
        return cmp < 0 ? InstallMode.Upgrade
             : cmp == 0 ? InstallMode.Repair
             : InstallMode.Downgrade;
    }

    /// <summary>
    /// Where a fresh install registers itself. A folder in the user's profile is always per-user;
    /// anywhere else is per-machine when Setup has admin rights (e.g. Program Files).
    /// </summary>
    public static InstallScope ScopeFor(string installDir, bool isElevated, string userProfileDir)
    {
        if (FileSystemHelpers.IsUnder(installDir, userProfileDir)) return InstallScope.User;
        return isElevated ? InstallScope.Machine : InstallScope.User;
    }

    /// <summary>Space needed on the target drive: the files are staged next to the install, plus a margin.</summary>
    public static long RequiredBytes(PayloadManifest manifest, long setupExeSize) =>
        (long)(manifest.TotalSize * 1.1) + setupExeSize + 20L * 1024 * 1024;

    // 2.0 and 2.0.0 are the same release.
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}

/// <summary>Everything the install/uninstall steps need, shared between them.</summary>
public sealed class InstallContext
{
    public InstallContext(InstallMode mode, string installDir, InstallScope scope)
    {
        Mode       = mode;
        InstallDir = Path.GetFullPath(installDir);
        Scope      = scope;
    }

    public InstallMode  Mode       { get; }
    public string       InstallDir { get; }
    public InstallScope Scope      { get; }

    public Payload?         Payload          { get; set; }
    public PayloadManifest? PreviousManifest { get; set; }
    public string           SetupExePath     { get; set; } = string.Empty;
    public bool             CreateDesktopShortcut { get; set; }

    /// <summary>
    /// Uninstall only: also delete %AppData%\SqlVitals (saved connections, logs) and
    /// %LocalAppData%\SqlVitals (monitoring history).
    /// </summary>
    public bool RemoveUserData { get; set; }

    /// <summary>Set when this run created the install folder, so a rollback can remove it again.</summary>
    public bool CreatedInstallDir { get; set; }

    /// <summary>Config files the user had changed and that were kept (the new default is saved as *.new).</summary>
    public List<string> PreservedConfigFiles { get; } = new();

    /// <summary>Files or folders uninstall left behind because Setup didn't create them.</summary>
    public List<string> LeftBehind { get; } = new();

    public string StagingDir => Path.Combine(InstallDir, ".setup-staging");
    public string BackupDir  => Path.Combine(InstallDir, ".setup-backup");
    public string AppExePath => Path.Combine(InstallDir, ProductInfo.AppExeName);
}
