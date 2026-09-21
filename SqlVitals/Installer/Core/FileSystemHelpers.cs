using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace SqlVitals.Installer.Core;

internal static class FileSystemHelpers
{
    public static string Sha256(string path)
    {
        using var sha    = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920);
        return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
    }

    /// <summary>
    /// Joins a manifest path onto <paramref name="root"/> and guarantees the result stays inside it
    /// (defence against "zip slip" even if a manifest check were bypassed).
    /// </summary>
    public static string SafeCombine(string root, string relativePath)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd('\\') + "\\";
        var full     = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', '\\')));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"'{relativePath}' points outside the install folder.");
        return full;
    }

    public static bool IsUnder(string path, string folder)
    {
        var fullFolder = Path.GetFullPath(folder).TrimEnd('\\') + "\\";
        var fullPath   = Path.GetFullPath(path);
        return fullPath.StartsWith(fullFolder, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(fullPath.TrimEnd('\\') + "\\", fullFolder, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Moves a file, replacing any file already at the destination.</summary>
    public static void MoveReplacing(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination))
        {
            File.SetAttributes(destination, FileAttributes.Normal);
            File.Delete(destination);
        }
        File.Move(source, destination);
    }

    public static void DeleteFileIfExists(string path)
    {
        if (!File.Exists(path)) return;
        File.SetAttributes(path, FileAttributes.Normal);
        File.Delete(path);
    }

    public static void DeleteDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }

    /// <summary>Removes empty folders below <paramref name="root"/> (deepest first), then root itself if empty.</summary>
    public static void DeleteEmptyDirectories(string root, bool includeRoot)
    {
        if (!Directory.Exists(root)) return;

        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)
                                     .OrderByDescending(d => d.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(dir).Any())
                Directory.Delete(dir);
        }

        if (includeRoot && !Directory.EnumerateFileSystemEntries(root).Any())
            Directory.Delete(root);
    }

    /// <summary>Nearest folder on the path that already exists, e.g. for free-space and permission checks.</summary>
    public static string? NearestExistingDirectory(string path)
    {
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(path));
            while (dir is not null && !dir.Exists)
                dir = dir.Parent;
            return dir?.FullName;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException)
        {
            return null;
        }
    }

    /// <summary>True when the current user can create files in <paramref name="directory"/> (or the folder that would contain it).</summary>
    public static bool CanWriteTo(string directory)
    {
        var existing = NearestExistingDirectory(directory);
        if (existing is null) return false;

        var probe = Path.Combine(existing, ".sqlvitals-setup-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose)) { }
            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            return false;
        }
    }

    public static string FormatSize(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):0.0} GB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):0} MB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):0} KB";
        return $"{bytes} bytes";
    }
}
