using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SqlVitals.Installer.Core;

internal static class ShortcutPaths
{
    public static (string StartMenu, string Desktop) For(InstallScope scope)
    {
        var programs = Environment.GetFolderPath(scope == InstallScope.Machine
            ? Environment.SpecialFolder.CommonPrograms
            : Environment.SpecialFolder.Programs);
        var desktop = Environment.GetFolderPath(scope == InstallScope.Machine
            ? Environment.SpecialFolder.CommonDesktopDirectory
            : Environment.SpecialFolder.DesktopDirectory);

        return (Path.Combine(programs, ProductInfo.ShortcutFileName),
                Path.Combine(desktop,  ProductInfo.ShortcutFileName));
    }
}

/// <summary>Creates .lnk files through the Windows Script Host shell object (always present on Windows).</summary>
internal static class ShellLink
{
    public static void Create(string shortcutPath, string targetPath, string workingDir, string description)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(shortcutPath)!);

        var shellType = Type.GetTypeFromProgID("WScript.Shell")
                        ?? throw new InvalidOperationException("Windows Script Host is not available to create shortcuts.");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(shortcutPath);
            try
            {
                link.TargetPath       = targetPath;
                link.WorkingDirectory = workingDir;
                link.Description      = description;
                link.IconLocation     = targetPath + ",0";
                link.Save();
            }
            finally
            {
                Marshal.FinalReleaseComObject(link);
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
