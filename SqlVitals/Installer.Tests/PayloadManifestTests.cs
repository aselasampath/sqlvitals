using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SqlVitals.Installer.Core;

namespace SqlVitals.Installer.Tests;

public class PayloadManifestTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Fact]
    public void Serialize_then_Parse_round_trips()
    {
        var original = new PayloadManifest(new Version(2, 0, 1), new[]
        {
            new ManifestFile("SqlVitals.Desktop.exe", Hash, 1234),
            new ManifestFile("appsettings.json", Hash, 56, preserveIfModified: true),
            new ManifestFile("runtimes/win-x64/native/lib with space.dll", Hash, 7),
        });

        var parsed = PayloadManifest.Parse(original.Serialize());

        Assert.Equal(original.Version, parsed.Version);
        Assert.Equal(original.Files.Select(f => (f.Path, f.Sha256, f.Size, f.PreserveIfModified)),
                     parsed.Files.Select(f => (f.Path, f.Sha256, f.Size, f.PreserveIfModified)));
        Assert.Equal(1297, parsed.TotalSize);
    }

    [Fact]
    public void Parse_accepts_CRLF_and_comments()
    {
        var parsed = PayloadManifest.Parse($"# comment\r\nversion\t1.2.3\r\nfile\t{Hash}\t1\t-\ta.dll\r\n");
        Assert.Equal(new Version(1, 2, 3), parsed.Version);
        Assert.Single(parsed.Files);
    }

    [Fact]
    public void Find_is_case_insensitive_and_accepts_backslashes()
    {
        var parsed = PayloadManifest.Parse($"version\t1.0\nfile\t{Hash}\t1\t-\tsub/App.dll\n");
        Assert.NotNull(parsed.Find(@"SUB\app.DLL"));
        Assert.Null(parsed.Find("other.dll"));
    }

    [Theory]
    [InlineData("../evil.dll")]
    [InlineData("sub/../../evil.dll")]
    [InlineData("C:/Windows/evil.dll")]
    [InlineData("/rooted.dll")]
    [InlineData("sub//double.dll")]
    [InlineData("./here.dll")]
    public void Parse_rejects_paths_that_could_escape_the_install_folder(string path)
    {
        Assert.Throws<InvalidDataException>(() =>
            PayloadManifest.Parse($"version\t1.0\nfile\t{Hash}\t1\t-\t{path}\n"));
    }

    [Theory]
    [InlineData("file\tnothex\t1\t-\ta.dll")]           // bad hash
    [InlineData("file\t" + Hash + "\t-5\t-\ta.dll")]    // negative size
    [InlineData("file\t" + Hash + "\t1\ta.dll")]        // missing column
    [InlineData("unknown\tline")]
    public void Parse_rejects_malformed_lines(string line)
    {
        Assert.Throws<InvalidDataException>(() => PayloadManifest.Parse($"version\t1.0\n{line}\n"));
    }

    [Fact]
    public void Parse_rejects_duplicates_and_missing_version()
    {
        Assert.Throws<InvalidDataException>(() =>
            PayloadManifest.Parse($"version\t1.0\nfile\t{Hash}\t1\t-\ta.dll\nfile\t{Hash}\t1\t-\tA.DLL\n"));
        Assert.Throws<InvalidDataException>(() =>
            PayloadManifest.Parse($"file\t{Hash}\t1\t-\ta.dll\n"));
    }

    [Fact]
    public void TryLoad_returns_null_for_missing_or_corrupt_files()
    {
        using var dir = new TempDir();
        Assert.Null(PayloadManifest.TryLoad(dir.File("missing.txt")));

        File.WriteAllText(dir.File("bad.txt"), "garbage");
        Assert.Null(PayloadManifest.TryLoad(dir.File("bad.txt")));
    }
}

public class PayloadTests
{
    [Fact]
    public void Open_and_extract_writes_every_manifest_file()
    {
        var files = new Dictionary<string, string>
        {
            ["SqlVitals.Desktop.exe"] = "exe",
            ["sub/folder/lib.dll"]    = "library",
        };
        using var payload = TestPayload.Open("2.0.0", files);
        using var dir = new TempDir();

        var reports = new List<double>();
        payload.ExtractTo(dir.Path, new SyncProgress(reports.Add), default);

        Assert.Equal("exe", File.ReadAllText(dir.File("SqlVitals.Desktop.exe")));
        Assert.Equal("library", File.ReadAllText(dir.File("sub/folder/lib.dll")));
        Assert.Equal(1.0, reports.Last(), 3);
    }

    [Fact]
    public void Extract_ignores_zip_entries_not_in_the_manifest()
    {
        var stream = TestPayload.Build("1.0", new Dictionary<string, string> { ["a.dll"] = "a" },
                                       tamper: zip => TestPayload.Write(zip, "app/extra.dll", "sneaky"));
        using var payload = Payload.Open(stream);
        using var dir = new TempDir();

        payload.ExtractTo(dir.Path, null, default);

        Assert.True(File.Exists(dir.File("a.dll")));
        Assert.False(File.Exists(dir.File("extra.dll")));
    }

    [Fact]
    public void Open_fails_when_a_manifest_file_is_missing_from_the_zip()
    {
        var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            TestPayload.Write(zip, "manifest.txt",
                $"version\t1.0\nfile\t{TestPayload.Sha256("x")}\t1\t-\tmissing.dll\n");
        ms.Position = 0;

        Assert.Throws<InvalidDataException>(() => Payload.Open(ms));
    }

    [Fact]
    public void Open_fails_without_a_manifest()
    {
        var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            TestPayload.Write(zip, "app/a.dll", "a");
        ms.Position = 0;

        Assert.Throws<InvalidDataException>(() => Payload.Open(ms));
    }
}

/// <summary>IProgress that reports synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
internal sealed class SyncProgress : IProgress<double>
{
    private readonly Action<double> _report;
    public SyncProgress(Action<double> report) => _report = report;
    public void Report(double value) => _report(value);
}
