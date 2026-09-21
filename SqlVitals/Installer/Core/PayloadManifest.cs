using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace SqlVitals.Installer.Core;

/// <summary>
/// The list of application files in a package, with their size and SHA-256 hash.
/// Written by build/Build-Installer.ps1 and copied into the install folder as
/// <see cref="ProductInfo.ManifestFileName"/> so later runs know exactly which files are Setup's.
/// </summary>
/// <remarks>
/// Plain tab-separated text rather than JSON keeps Setup free of extra assemblies:
/// <code>
/// version	2.0.0
/// file	&lt;sha256&gt;	&lt;size&gt;	&lt;flags&gt;	&lt;relative/path&gt;
/// </code>
/// The path is last so it may contain spaces. Flags are "-" or "preserve" (a config file the
/// user may have edited, which upgrades keep).
/// </remarks>
public sealed class PayloadManifest
{
    public Version Version { get; }
    public IReadOnlyList<ManifestFile> Files { get; }

    public PayloadManifest(Version version, IEnumerable<ManifestFile> files)
    {
        Version = version ?? throw new ArgumentNullException(nameof(version));
        Files   = files.ToList();
    }

    public long TotalSize => Files.Sum(f => f.Size);

    public ManifestFile? Find(string relativePath) =>
        Files.FirstOrDefault(f => string.Equals(f.Path, NormalizePath(relativePath), StringComparison.OrdinalIgnoreCase));

    public static PayloadManifest Parse(string text)
    {
        Version? version = null;
        var files = new List<ManifestFile>();
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var lineNo = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            lineNo++;
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            var parts = line.Split('\t');
            switch (parts[0])
            {
                case "version" when parts.Length == 2 && Version.TryParse(parts[1], out var v):
                    version = v;
                    break;

                case "file" when parts.Length == 5:
                    if (!IsSha256(parts[1]) ||
                        !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                        throw new InvalidDataException($"Manifest line {lineNo} is malformed.");

                    var path = NormalizePath(parts[4]);
                    if (!IsSafeRelativePath(path))
                        throw new InvalidDataException($"Manifest line {lineNo} has an unsafe path.");
                    if (!seen.Add(path))
                        throw new InvalidDataException($"Manifest line {lineNo} repeats '{path}'.");

                    files.Add(new ManifestFile(path, parts[1].ToLowerInvariant(), size,
                                               preserveIfModified: parts[3] == "preserve"));
                    break;

                default:
                    throw new InvalidDataException($"Manifest line {lineNo} is not recognised.");
            }
        }

        if (version is null)
            throw new InvalidDataException("Manifest has no version.");

        return new PayloadManifest(version, files);
    }

    public string Serialize()
    {
        var sb = new StringBuilder();
        sb.Append("# SqlVitals install manifest\n");
        sb.Append("version\t").Append(Version).Append('\n');
        foreach (var f in Files)
        {
            sb.Append("file\t").Append(f.Sha256)
              .Append('\t').Append(f.Size.ToString(CultureInfo.InvariantCulture))
              .Append('\t').Append(f.PreserveIfModified ? "preserve" : "-")
              .Append('\t').Append(f.Path).Append('\n');
        }
        return sb.ToString();
    }

    public static PayloadManifest? TryLoad(string path)
    {
        try
        {
            return File.Exists(path) ? Parse(File.ReadAllText(path, Encoding.UTF8)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }

    internal static string NormalizePath(string path) => path.Replace('\\', '/');

    /// <summary>Rejects rooted paths, drive letters and ".." so no entry can land outside the install folder.</summary>
    internal static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf(':') >= 0 || path.StartsWith("/", StringComparison.Ordinal))
            return false;
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return false;
        return path.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");
    }

    private static bool IsSha256(string s) =>
        s.Length == 64 && s.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
}

public sealed class ManifestFile
{
    public ManifestFile(string path, string sha256, long size, bool preserveIfModified = false)
    {
        Path               = path;
        Sha256             = sha256;
        Size               = size;
        PreserveIfModified = preserveIfModified;
    }

    /// <summary>Relative to the install folder, forward slashes.</summary>
    public string Path               { get; }
    public string Sha256             { get; }
    public long   Size               { get; }
    public bool   PreserveIfModified { get; }
}
