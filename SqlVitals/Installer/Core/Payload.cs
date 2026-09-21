using System;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Threading;

namespace SqlVitals.Installer.Core;

/// <summary>
/// The application files embedded in Setup: a zip with <c>manifest.txt</c> at the root and the
/// published app under <c>app/</c>. Nothing is downloaded at install time, so what gets installed
/// is exactly what was built and (when signed) covered by Setup's Authenticode signature.
/// </summary>
public sealed class Payload : IDisposable
{
    public const string ResourceName = "SqlVitals.payload.zip";
    private const string ManifestEntry = "manifest.txt";
    private const string AppPrefix     = "app/";

    private readonly ZipArchive _zip;

    public PayloadManifest Manifest { get; }

    private Payload(ZipArchive zip, PayloadManifest manifest)
    {
        _zip     = zip;
        Manifest = manifest;
    }

    /// <summary>Opens the payload embedded in this Setup, or returns null for a build without one.</summary>
    public static Payload? OpenEmbedded()
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        return stream is null ? null : Open(stream);
    }

    public static Payload Open(Stream stream)
    {
        var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        try
        {
            var entry = zip.GetEntry(ManifestEntry)
                        ?? throw new InvalidDataException("The package has no manifest.");
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            var manifest = PayloadManifest.Parse(reader.ReadToEnd());

            foreach (var file in manifest.Files)
            {
                if (FindEntry(zip, file.Path) is null)
                    throw new InvalidDataException($"The package is missing '{file.Path}'.");
            }

            return new Payload(zip, manifest);
        }
        catch
        {
            zip.Dispose();
            throw;
        }
    }

    /// <summary>Writes every manifest file under <paramref name="targetDir"/>. Entries not in the manifest are ignored.</summary>
    public void ExtractTo(string targetDir, IProgress<double>? progress, CancellationToken ct)
    {
        long done  = 0;
        var  total = Math.Max(1, Manifest.TotalSize);
        var  buffer = new byte[81920];

        foreach (var file in Manifest.Files)
        {
            ct.ThrowIfCancellationRequested();

            var entry  = FindEntry(_zip, file.Path)!;
            var target = FileSystemHelpers.SafeCombine(targetDir, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            using (var input  = entry.Open())
            using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                    done += read;
                    progress?.Report((double)done / total);
                }
            }
        }
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, string relativePath) =>
        zip.GetEntry(AppPrefix + relativePath) ?? zip.GetEntry((AppPrefix + relativePath).Replace('/', '\\'));

    public void Dispose() => _zip.Dispose();
}
