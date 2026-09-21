using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SqlVitals.Installer.Core;

namespace SqlVitals.Installer.Tests;

/// <summary>
/// Runs the file steps (unpack → verify → commit) against a scratch folder. Registry and
/// shortcut steps touch the real machine and are left to manual testing.
/// </summary>
public class InstallStepsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly string  _setupExe;

    public InstallStepsTests()
    {
        _setupExe = Path.Combine(Path.GetTempPath(), "sqlvitals-setup-tests", Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(_setupExe, "setup");
    }

    public void Dispose()
    {
        _dir.Dispose();
        File.Delete(_setupExe);
    }

    private string InstallDir => Path.Combine(_dir.Path, "SqlVitals");

    private static readonly Dictionary<string, string> V1 = new()
    {
        ["SqlVitals.Desktop.exe"] = "app v1",
        ["appsettings.json"]      = "{ \"v\": 1 }",
        ["old-only.dll"]          = "dropped in v2",
        ["sub/lib.dll"]           = "lib v1",
    };

    private static readonly Dictionary<string, string> V2 = new()
    {
        ["SqlVitals.Desktop.exe"] = "app v2",
        ["appsettings.json"]      = "{ \"v\": 2 }",
        ["sub/lib.dll"]           = "lib v2",
        ["new-in-v2.dll"]         = "new",
    };

    private static readonly HashSet<string> Preserve = new() { "appsettings.json" };

    private (InstallContext Context, CommitFilesStep Commit) RunFileSteps(
        InstallMode mode, Dictionary<string, string> files, PayloadManifest? previous = null)
    {
        var context = new InstallContext(mode, InstallDir, InstallScope.User)
        {
            Payload          = TestPayload.Open(mode == InstallMode.Install ? "1.0.0" : "2.0.0", files, Preserve),
            PreviousManifest = previous,
            SetupExePath     = _setupExe,
        };

        var commit = new CommitFilesStep();
        var progress = new SyncProgress(_ => { });
        new ExtractPayloadStep().Execute(context, progress, CancellationToken.None);
        new VerifyFilesStep().Execute(context, progress, CancellationToken.None);
        commit.Execute(context, progress, CancellationToken.None);
        return (context, commit);
    }

    private PayloadManifest InstallV1()
    {
        var (context, _) = RunFileSteps(InstallMode.Install, V1);
        new CleanupStep().Execute(context, new SyncProgress(_ => { }), CancellationToken.None);
        return PayloadManifest.TryLoad(Path.Combine(InstallDir, ProductInfo.ManifestFileName))!;
    }

    private string Read(string relative) => File.ReadAllText(Path.Combine(InstallDir, relative.Replace('/', '\\')));

    private Dictionary<string, string> Snapshot() =>
        Directory.EnumerateFiles(InstallDir, "*", SearchOption.AllDirectories)
                 .ToDictionary(f => f.Substring(InstallDir.Length + 1), File.ReadAllText);

    [Fact]
    public void Fresh_install_places_files_setup_copy_and_manifest()
    {
        var manifest = InstallV1();

        Assert.Equal("app v1", Read("SqlVitals.Desktop.exe"));
        Assert.Equal("lib v1", Read("sub/lib.dll"));
        Assert.Equal("setup", Read(ProductInfo.SetupExeName));
        Assert.Equal(new Version(1, 0, 0), manifest.Version);
        Assert.False(Directory.Exists(Path.Combine(InstallDir, ".setup-staging")));
        Assert.False(Directory.Exists(Path.Combine(InstallDir, ".setup-backup")));
    }

    [Fact]
    public void Upgrade_replaces_files_adds_new_ones_and_removes_files_the_new_version_dropped()
    {
        var previous = InstallV1();
        File.WriteAllText(Path.Combine(InstallDir, "my-notes.txt"), "user file");

        var (context, _) = RunFileSteps(InstallMode.Upgrade, V2, previous);

        Assert.Equal("app v2", Read("SqlVitals.Desktop.exe"));
        Assert.Equal("{ \"v\": 2 }", Read("appsettings.json"));   // unmodified config takes the new default
        Assert.Equal("new", Read("new-in-v2.dll"));
        Assert.False(File.Exists(Path.Combine(InstallDir, "old-only.dll")));
        Assert.Equal("user file", Read("my-notes.txt"));             // never touched: not Setup's
        Assert.Empty(context.PreservedConfigFiles);
    }

    [Fact]
    public void Upgrade_keeps_a_config_file_the_user_edited_and_saves_the_new_default_beside_it()
    {
        var previous = InstallV1();
        File.WriteAllText(Path.Combine(InstallDir, "appsettings.json"), "{ \"mine\": true }");

        var (context, _) = RunFileSteps(InstallMode.Upgrade, V2, previous);

        Assert.Equal("{ \"mine\": true }", Read("appsettings.json"));
        Assert.Equal("{ \"v\": 2 }", Read("appsettings.json.new"));
        Assert.Equal(new[] { "appsettings.json" }, context.PreservedConfigFiles);
    }

    [Fact]
    public void Repair_restores_a_deleted_file()
    {
        var previous = InstallV1();
        File.Delete(Path.Combine(InstallDir, "sub", "lib.dll"));

        RunFileSteps(InstallMode.Repair, V1, previous);

        Assert.Equal("lib v1", Read("sub/lib.dll"));
    }

    [Fact]
    public void Rolling_back_an_upgrade_restores_the_previous_installation_exactly()
    {
        var previous = InstallV1();
        File.WriteAllText(Path.Combine(InstallDir, "appsettings.json"), "{ \"mine\": true }");
        var before = Snapshot();

        var (context, commit) = RunFileSteps(InstallMode.Upgrade, V2, previous);
        commit.Rollback(context);
        new ExtractPayloadStep().Rollback(context);
        new CleanupStep().Execute(context, new SyncProgress(_ => { }), CancellationToken.None);

        Assert.Equal(before, Snapshot());
    }

    [Fact]
    public void Rolling_back_a_fresh_install_removes_the_folder_it_created()
    {
        var (context, commit) = RunFileSteps(InstallMode.Install, V1);
        commit.Rollback(context);
        new ExtractPayloadStep().Rollback(context);
        FileSystemHelpers.DeleteDirectoryIfExists(context.BackupDir);
        FileSystemHelpers.DeleteEmptyDirectories(context.InstallDir, includeRoot: true);

        Assert.False(Directory.Exists(InstallDir));
    }

    [Fact]
    public void Verify_rejects_a_file_that_does_not_match_its_hash()
    {
        var context = new InstallContext(InstallMode.Install, InstallDir, InstallScope.User)
        {
            Payload      = TestPayload.Open("1.0.0", V1, Preserve),
            SetupExePath = _setupExe,
        };
        var progress = new SyncProgress(_ => { });
        new ExtractPayloadStep().Execute(context, progress, CancellationToken.None);
        File.WriteAllText(Path.Combine(context.StagingDir, "sub", "lib.dll"), "tampered");

        var ex = Assert.Throws<InstallStepException>(() =>
            new VerifyFilesStep().Execute(context, progress, CancellationToken.None));
        Assert.Contains("integrity", ex.Message);
    }

    [Fact]
    public void Uninstall_removes_only_the_files_setup_installed()
    {
        InstallV1();
        File.WriteAllText(Path.Combine(InstallDir, "my-notes.txt"), "user file");
        var context = new InstallContext(InstallMode.Uninstall, InstallDir, InstallScope.User);

        new RemoveFilesStep().Execute(context, new SyncProgress(_ => { }), CancellationToken.None);

        Assert.Equal(new[] { "my-notes.txt" }, Snapshot().Keys);
        Assert.Equal(new[] { InstallDir }, context.LeftBehind);

        File.Delete(Path.Combine(InstallDir, "my-notes.txt"));
        new RemoveFilesStep().Execute(context, new SyncProgress(_ => { }), CancellationToken.None);
        Assert.False(Directory.Exists(InstallDir));
    }

    [Fact]
    public void IsUserModified_compares_against_what_was_installed()
    {
        var target = Path.Combine(_dir.Path, "appsettings.json");
        var incoming = new ManifestFile("appsettings.json", TestPayload.Sha256("new"), 3, true);
        var previous = new PayloadManifest(new Version(1, 0), new[]
            { new ManifestFile("appsettings.json", TestPayload.Sha256("old"), 3, true) });

        Assert.False(CommitFilesStep.IsUserModified(target, incoming, previous));   // missing

        File.WriteAllText(target, "old");
        Assert.False(CommitFilesStep.IsUserModified(target, incoming, previous));   // as installed

        File.WriteAllText(target, "new");
        Assert.False(CommitFilesStep.IsUserModified(target, incoming, previous));   // already the new default

        File.WriteAllText(target, "edited");
        Assert.True(CommitFilesStep.IsUserModified(target, incoming, previous));
        Assert.True(CommitFilesStep.IsUserModified(target, incoming, null));        // no record: assume the user's
    }
}
