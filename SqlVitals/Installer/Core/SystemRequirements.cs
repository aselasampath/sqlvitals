using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace SqlVitals.Installer.Core;

public enum CheckStatus
{
    Passed,
    Warning,
    Failed,
}

/// <summary>One line on the system-check screen. <see cref="Resolution"/> tells the user how to fix a problem.</summary>
public sealed class RequirementResult
{
    public RequirementResult(string name, CheckStatus status, string detail, string? resolution = null)
    {
        Name       = name;
        Status     = status;
        Detail     = detail;
        Resolution = resolution;
    }

    public string      Name       { get; }
    public CheckStatus Status     { get; }
    public string      Detail     { get; }
    public string?     Resolution { get; }

    /// <summary>The failure can be fixed by restarting Setup as administrator.</summary>
    public bool NeedsElevation { get; set; }
}

/// <summary>What the checks are run against.</summary>
public sealed class RequirementInput
{
    public string           InstallDir    { get; set; } = string.Empty;
    public long             RequiredBytes { get; set; }
    public PayloadManifest? Manifest      { get; set; }
    public string?          PayloadError  { get; set; }
    public bool             IsElevated    { get; set; }
    public ExistingInstallation? Existing { get; set; }
}

public static class SystemRequirements
{
    // .NET 8 (bundled with the app) supports Windows 10 1607 (build 14393) and later.
    internal const int MinimumWindowsBuild = 14393;

    public static IReadOnlyList<RequirementResult> Evaluate(RequirementInput input) => new[]
    {
        CheckWindowsVersion(GetWindowsVersion()),
        CheckArchitecture(Environment.Is64BitOperatingSystem),
        CheckPackage(input.Manifest, input.PayloadError),
        CheckRuntime(input.Manifest),
        CheckDiskSpace(input.InstallDir, input.RequiredBytes, GetFreeSpace(input.InstallDir)),
        CheckPermissions(input.InstallDir, FileSystemHelpers.CanWriteTo(input.InstallDir), input),
        CheckNotRunning(RunningAppProcesses(input.InstallDir)),
    };

    public static bool CanContinue(IEnumerable<RequirementResult> results) =>
        results.All(r => r.Status != CheckStatus.Failed);

    // ── Individual checks (pure, so they can be unit tested) ─────────────────

    internal static RequirementResult CheckWindowsVersion(Version os)
    {
        const string name = "Windows version";
        var label = os.Major >= 10 && os.Build >= 22000 ? $"Windows 11 (build {os.Build})"
                  : os.Major >= 10                     ? $"Windows 10 (build {os.Build})"
                  : $"Windows {os.Major}.{os.Minor}";

        return os.Major > 10 || (os.Major == 10 && os.Build >= MinimumWindowsBuild)
            ? new RequirementResult(name, CheckStatus.Passed, label)
            : new RequirementResult(name, CheckStatus.Failed,
                $"{label} is not supported. SqlVitals needs Windows 10 version 1607 or later, or Windows 11.",
                "Update Windows through Settings → Windows Update, or install SqlVitals on a newer PC.");
    }

    internal static RequirementResult CheckArchitecture(bool is64Bit)
    {
        const string name = "64-bit Windows";
        return is64Bit
            ? new RequirementResult(name, CheckStatus.Passed, "This PC runs 64-bit Windows.")
            : new RequirementResult(name, CheckStatus.Failed,
                "This PC runs 32-bit Windows. SqlVitals is a 64-bit application.",
                "Install SqlVitals on a PC with 64-bit Windows 10 or 11.");
    }

    internal static RequirementResult CheckPackage(PayloadManifest? manifest, string? error)
    {
        const string name = "Installation package";
        return manifest is not null
            ? new RequirementResult(name, CheckStatus.Passed,
                $"SqlVitals {manifest.Version.ToString(3)} — {manifest.Files.Count} files, {FileSystemHelpers.FormatSize(manifest.TotalSize)}.")
            : new RequirementResult(name, CheckStatus.Failed,
                "The application files in this Setup are missing or damaged" + (error is null ? "." : $": {error}"),
                "Download SqlVitals Setup again from your official SqlVitals release page and run the new copy.");
    }

    /// <summary>The .NET 8 runtime ships inside the package, so the user never installs it separately.</summary>
    internal static RequirementResult CheckRuntime(PayloadManifest? manifest)
    {
        const string name = ".NET runtime";
        if (manifest is null)
            return new RequirementResult(name, CheckStatus.Failed, "Can't check: the installation package couldn't be read.",
                "Download SqlVitals Setup again and run the new copy.");

        var bundled = new[] { "hostfxr.dll", "coreclr.dll", "wpfgfx_cor3.dll" }
            .All(dll => manifest.Files.Any(f => string.Equals(Path.GetFileName(f.Path), dll, StringComparison.OrdinalIgnoreCase)));

        return bundled
            ? new RequirementResult(name, CheckStatus.Passed, "Included with SqlVitals — nothing extra to install.")
            : new RequirementResult(name, CheckStatus.Failed,
                "This package doesn't include the .NET 8 desktop runtime SqlVitals needs.",
                "This Setup was built incorrectly. Download the official SqlVitals Setup and run it instead.");
    }

    internal static RequirementResult CheckDiskSpace(string installDir, long required, long? free)
    {
        const string name = "Disk space";
        var drive = SafeRoot(installDir);

        if (free is null)
            return new RequirementResult(name, CheckStatus.Warning,
                $"Couldn't read the free space on {drive}. About {FileSystemHelpers.FormatSize(required)} is needed.",
                "Make sure the drive is connected and has enough free space.");

        return free.Value >= required
            ? new RequirementResult(name, CheckStatus.Passed,
                $"{FileSystemHelpers.FormatSize(free.Value)} free on {drive} — {FileSystemHelpers.FormatSize(required)} needed.")
            : new RequirementResult(name, CheckStatus.Failed,
                $"Only {FileSystemHelpers.FormatSize(free.Value)} free on {drive}; SqlVitals needs {FileSystemHelpers.FormatSize(required)}.",
                $"Free up at least {FileSystemHelpers.FormatSize(required - free.Value)} on {drive} (for example with Settings → System → Storage), " +
                "or choose a different drive on the next screen, then click Check again.");
    }

    internal static RequirementResult CheckPermissions(string installDir, bool canWrite, RequirementInput input)
    {
        const string name = "Permissions";
        if (canWrite)
            return new RequirementResult(name, CheckStatus.Passed, $"You can install to {installDir}.");

        // An install made by an administrator can only be changed with admin rights.
        if (input.Existing is { Scope: InstallScope.Machine } && !input.IsElevated)
        {
            return new RequirementResult(name, CheckStatus.Failed,
                "SqlVitals was installed for all users, so changing it needs administrator rights.",
                "Click \"Restart as administrator\" and approve the Windows prompt.")
            { NeedsElevation = true };
        }

        // A fresh install can simply go somewhere else; the next screen lets the user choose.
        var fresh = input.Existing is null;
        return new RequirementResult(name, fresh ? CheckStatus.Warning : CheckStatus.Failed,
            $"You don't have permission to write to {installDir}.",
            fresh
                ? "Choose a folder you own on the next screen (the default in your user profile needs no special rights), or restart Setup as administrator."
                : "Click \"Restart as administrator\", or ask your IT administrator for write access to that folder.")
        { NeedsElevation = !input.IsElevated };
    }

    /// <summary>
    /// How to quit SqlVitals. From 0.35 closing its window can leave it running in the
    /// notification area, where it's easy to miss.
    /// </summary>
    internal const string QuitAppAdvice =
        "Save any work and close every SqlVitals window. If its icon is still in the notification area " +
        "(beside the clock; it may be under ^), right-click it and choose Exit.";

    internal static RequirementResult CheckNotRunning(IReadOnlyCollection<int> processIds)
    {
        const string name = "SqlVitals not running";
        return processIds.Count == 0
            ? new RequirementResult(name, CheckStatus.Passed, "No running copy of SqlVitals will be affected.")
            : new RequirementResult(name, CheckStatus.Failed,
                "SqlVitals is open, so its files can't be replaced.",
                QuitAppAdvice + " Then click Check again.");
    }

    // ── System probes ─────────────────────────────────────────────────────────

    /// <summary>Running SqlVitals processes whose exe lives in <paramref name="installDir"/>.</summary>
    public static IReadOnlyCollection<int> RunningAppProcesses(string installDir)
    {
        var ids = new List<int>();
        foreach (var process in Process.GetProcessesByName(ProductInfo.AppProcessName))
        {
            using (process)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (path is null || FileSystemHelpers.IsUnder(path, installDir))
                        ids.Add(process.Id);
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Can't see its path (e.g. an elevated copy): assume it's ours rather than risk a locked file.
                    ids.Add(process.Id);
                }
            }
        }
        return ids;
    }

    /// <summary>Bytes free for the current user on the drive holding <paramref name="installDir"/>, or null if unknown.</summary>
    public static long? GetFreeSpace(string installDir)
    {
        try
        {
            var existing = FileSystemHelpers.NearestExistingDirectory(installDir);
            if (existing is null) return null;
            return GetDiskFreeSpaceEx(existing, out var available, out _, out _) ? (long)available : null;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            return null;
        }
    }

    private static string SafeRoot(string path)
    {
        try { return Path.GetPathRoot(Path.GetFullPath(path)) ?? path; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    /// <summary>Real OS version; Environment.OSVersion can be capped by compatibility shims.</summary>
    private static Version GetWindowsVersion()
    {
        var info = new OsVersionInfoEx { dwOSVersionInfoSize = Marshal.SizeOf(typeof(OsVersionInfoEx)) };
        return RtlGetVersion(ref info) == 0
            ? new Version(info.dwMajorVersion, info.dwMinorVersion, info.dwBuildNumber)
            : Environment.OSVersion.Version;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong freeBytesAvailable,
                                                  out ulong totalBytes, out ulong totalFreeBytes);

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref OsVersionInfoEx versionInfo);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersionInfoEx
    {
        public int dwOSVersionInfoSize;
        public int dwMajorVersion;
        public int dwMinorVersion;
        public int dwBuildNumber;
        public int dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szCSDVersion;
        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;
    }
}
