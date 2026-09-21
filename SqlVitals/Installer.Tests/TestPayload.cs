using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SqlVitals.Installer.Core;

namespace SqlVitals.Installer.Tests;

/// <summary>Builds small in-memory packages in the same layout Build-Installer.ps1 produces.</summary>
internal static class TestPayload
{
    public static MemoryStream Build(string version, IDictionary<string, string> files,
                                     ISet<string>? preserve = null, Action<ZipArchive>? tamper = null)
    {
        var manifest = new PayloadManifest(new Version(version), files.Select(f =>
            new ManifestFile(f.Key, Sha256(f.Value), Encoding.UTF8.GetByteCount(f.Value),
                             preserve?.Contains(f.Key) == true)));

        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "manifest.txt", manifest.Serialize());
            foreach (var f in files)
                Write(zip, "app/" + f.Key, f.Value);
            tamper?.Invoke(zip);
        }
        stream.Position = 0;
        return stream;
    }

    public static Payload Open(string version, IDictionary<string, string> files, ISet<string>? preserve = null) =>
        Payload.Open(Build(version, files, preserve));

    public static void Write(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    public static string Sha256(string content)
    {
        using var sha = SHA256.Create();
        return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(content)).Select(b => b.ToString("x2")));
    }
}

/// <summary>A scratch folder under %TEMP% that deletes itself.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sqlvitals-setup-tests", Guid.NewGuid().ToString("N"));

    public string File(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', '\\'));

    public void Dispose()
    {
        try { FileSystemHelpers.DeleteDirectoryIfExists(Path); }
        catch (IOException) { }
    }
}
