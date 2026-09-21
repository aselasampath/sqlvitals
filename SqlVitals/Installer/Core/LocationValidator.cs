using System;
using System.IO;
using System.Linq;

namespace SqlVitals.Installer.Core;

/// <summary>Checks a user-entered install folder and explains, in plain words, what's wrong with it.</summary>
public static class LocationValidator
{
    /// <summary>Returns an error message, or null when the path is usable. <paramref name="fullPath"/> is the normalised folder.</summary>
    public static string? CheckPath(string? input, out string fullPath)
    {
        fullPath = string.Empty;
        var text = (input ?? string.Empty).Trim().Trim('"');

        if (text.Length == 0)
            return "Enter the folder to install SqlVitals into.";

        if (text.StartsWith(@"\\", StringComparison.Ordinal))
            return "Network folders aren't supported. Choose a folder on this PC.";

        if (text.Length < 3 || !char.IsLetter(text[0]) || text[1] != ':' || (text[2] != '\\' && text[2] != '/'))
            return $@"Enter a full folder path, for example {ProductInfo.DefaultInstallDir}.";

        try
        {
            fullPath = Path.GetFullPath(text).TrimEnd('\\');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "That folder name isn't valid. Folder names can't contain  < > | ? * or \".";
        }

        if (fullPath.Length <= 2)   // "C:" after trimming the slash
            return $@"Choose a folder rather than the whole drive, for example {fullPath}\{ProductInfo.Name}.";

        if (fullPath.Length > 150)
            return "That path is too long. Choose a shorter folder path.";

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows) && FileSystemHelpers.IsUnder(fullPath, windows))
            return "Choose a folder outside the Windows folder.";

        return null;
    }

    /// <summary>
    /// True when the folder already holds files that aren't a SqlVitals installation. Installing
    /// there would mix SqlVitals with someone else's files, so a fresh install asks for another folder.
    /// </summary>
    public static bool IsOccupiedByOtherFiles(string fullPath)
    {
        try
        {
            if (!Directory.Exists(fullPath)) return false;
            if (File.Exists(Path.Combine(fullPath, ProductInfo.ManifestFileName))) return false;
            return Directory.EnumerateFileSystemEntries(fullPath).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;   // the permission check reports this more usefully
        }
    }

    /// <summary>When the user browses to a parent folder, install into a SqlVitals subfolder of it.</summary>
    public static string WithProductFolder(string chosen)
    {
        var trimmed = chosen.TrimEnd('\\');
        return string.Equals(Path.GetFileName(trimmed), ProductInfo.Name, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : Path.Combine(trimmed.Length == 2 ? trimmed + "\\" : trimmed, ProductInfo.Name);
    }
}
