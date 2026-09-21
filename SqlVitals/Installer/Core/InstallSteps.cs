using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace SqlVitals.Installer.Core;

/// <summary>The ordered steps for installing, upgrading or repairing SqlVitals.</summary>
public static class InstallSteps
{
    public static IReadOnlyList<IInstallStep> ForInstall() => new IInstallStep[]
    {
        new EnsureAppClosedStep(),
        new ExtractPayloadStep(),
        new VerifyFilesStep(),
        new CommitFilesStep(),
        new ShortcutsStep(),
        new RegisterStep(),
        new CleanupStep(),
    };

    public static IReadOnlyList<IInstallStep> ForUninstall() => new IInstallStep[]
    {
        new EnsureAppClosedStep(),
        new RemoveShortcutsStep(),
        new RemoveFilesStep(),
        new UnregisterStep(),
        new RemoveUserDataStep(),
    };
}

/// <summary>Files can't be replaced while SqlVitals has them open.</summary>
internal sealed class EnsureAppClosedStep : IInstallStep
{
    public string Title  => "Checking SqlVitals is closed";
    public double Weight => 1;

    public void Execute(InstallContext context, IProgress<double> progress, CancellationToken ct)
    {
        if (SystemRequirements.RunningAppProcesses(context.InstallDir).Count > 0)
            throw new InstallStepException(
                "SqlVitals is open, so its files can't be changed.",
                "Save any work, close every SqlVitals window, then click Retry.");
    }

    public void Rollback(InstallContext context) { }
}

/// <summary>Unpacks the embedded files into a staging folder beside the install (same drive, so moves are instant).</summary>
internal sealed class ExtractPayloadStep : IInstallStep
{
    public string Title  => "Unpacking application files";
    public double Weight => 50;

    public void Execute(InstallContext context, IProgress<double> progress, CancellationToken ct)
    {
        var payload = context.Payload ?? throw new InvalidDataException("No installation package is loaded.");

        if (!Directory.Exists(context.InstallDir))
        {
            Directory.CreateDirectory(context.InstallDir);
            context.CreatedInstallDir = true;
        }

        FileSystemHelpers.DeleteDirectoryIfExists(context.StagingDir);
        PreserveLeftoverBackup(context);

        var staging = Directory.CreateDirectory(context.StagingDir);
        staging.Attributes |= FileAttributes.Hidden;

        payload.ExtractTo(context.StagingDir, progress, ct);
    }

    public void Rollback(InstallContext context)
    {
        FileSystemHelpers.DeleteDirectoryIfExists(context.StagingDir);
        if (context.CreatedInstallDir)
            FileSystemHelpers.DeleteEmptyDirectories(context.InstallDir, includeRoot: true);
    }

    // A backup folder left by an interrupted run may hold the only copy of the user's previous
    // files, so move it aside rather than overwrite it.
    private static void PreserveLeftoverBackup(InstallContext context)
    {
        if (!Directory.Exists(context.BackupDir)) return;
        var keep = context.BackupDir + "-" + DateTime.Now.ToString("yyyyMMddHHmmss");
        Directory.Move(context.BackupDir, keep);
        SetupLog.Warn($"Found a backup from an interrupted run; kept it at {keep}");
    }
}

/// <summary>Confirms every unpacked file matches the size and SHA-256 hash recorded when the package was built.</summary>
internal sealed class VerifyFilesStep : IInstallStep
{
    public string Title  => "Verifying files";
    public double Weight => 15;

    public void Execute(InstallContext context, IProgress<double> progress, CancellationToken ct)
    {
        var files = context.Payload!.Manifest.Files;
        for (var i = 0; i < files.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var file = files[i];
            var path = FileSystemHelpers.SafeCombine(context.StagingDir, file.Path);

            if (!File.Exists(path) || new FileInfo(path).Length != file.Size ||
                !string.Equals(FileSystemHelpers.Sha256(path), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                SetupLog.Error($"Hash mismatch: {file.Path}");
                throw new InstallStepException(
                    $"A file in the installation package failed its integrity check ({file.Path}).",
                    "The download may be incomplete or altered. Cancel, download SqlVitals Setup again from your official release page, and run the new copy.");
            }

            progress.Report((i + 1) / (double)files.Count);
        }
    }

    public void Rollback(InstallContext context) { }
}

/// <summary>
/// Swaps the verified files into place. Every file it replaces or removes is moved into a backup
/// folder first, so a failure or cancel puts the previous installation back exactly.
/// Config files the user edited are kept; the new default is written beside them as *.new.
/// </summary>
internal sealed class CommitFilesStep : IInstallStep
{
    private enum Change { Added, Replaced, Removed }

    private readonly List<(Change Kind, string Target, string? Backup)> _journal = new();

    public string Title  => "Installing application files";
    public double Weight => 25;

    public void Execute(InstallContext context, IProgress<double> progress, CancellationToken ct)
    {
        _journal.Clear();
        var manifest = context.Payload!.Manifest;
        var previous = context.PreviousManifest;
        var total    = manifest.Files.Count + 3.0;
        var done     = 0;

        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            var target = FileSystemHelpers.SafeCombine(context.InstallDir, file.Path);
            var staged = FileSystemHelpers.SafeCombine(context.StagingDir, file.Path);

            if (file.PreserveIfModified && IsUserModified(target, file, previous))
            {
                // Keep the user's copy; offer the new default next to it.
                Place(context, staged, target + ".new");
                context.PreservedConfigFiles.Add(file.Path);
                SetupLog.Info($"Kept modified config: {file.Path}");
            }
            else
            {
                Place(context, staged, target);
            }

            progress.Report(++done / total);
        }

        // Files the previous version installed that this one no longer ships.
        if (previous is not null)
        {
            foreach (var old in previous.Files.Where(f => manifest.Find(f.Path) is null))
            {
                var target = FileSystemHelpers.SafeCombine(context.InstallDir, old.Path);
                if (!File.Exists(target)) continue;
                var backup = BackupPathFor(context, target);
                FileSystemHelpers.MoveReplacing(target, backup);
                _journal.Add((Change.Removed, target, backup));
            }
        }
        progress.Report(++done / total);

        // A copy of Setup for Modify/Uninstall from Windows Settings.
        var setupTarget = Path.Combine(context.InstallDir, ProductInfo.SetupExeName);
        if (!string.Equals(Path.GetFullPath(context.SetupExePath), setupTarget, StringComparison.OrdinalIgnoreCase))
        {
            var tmp = Path.Combine(context.StagingDir, ProductInfo.SetupExeName);
            File.Copy(context.SetupExePath, tmp, overwrite: true);
            Place(context, tmp, setupTarget);
        }
        progress.Report(++done / total);

        // Record what is now installed — written last so it only ever describes a complete install.
        var manifestTmp = Path.Combine(context.StagingDir, ProductInfo.ManifestFileName);
        File.WriteAllText(manifestTmp, manifest.Serialize(), new UTF8Encoding(false));
        Place(context, manifestTmp, Path.Combine(context.InstallDir, ProductInfo.ManifestFileName));
        progress.Report(1);
    }

    public void Rollback(InstallContext context)
    {
        List<Exception>? errors = null;
        for (var i = _journal.Count - 1; i >= 0; i--)
        {
            var (kind, target, backup) = _journal[i];
            try
            {
                if (kind is Change.Added or Change.Replaced)
                    FileSystemHelpers.DeleteFileIfExists(target);
                if (kind is Change.Replaced or Change.Removed && backup is not null)
                    FileSystemHelpers.MoveReplacing(backup, target);
            }
            catch (Exception ex)
            {
                (errors ??= new List<Exception>()).Add(ex);
                SetupLog.Error($"Could not restore {target}", ex);
            }
        }
        _journal.Clear();
        context.PreservedConfigFiles.Clear();

        if (errors is not null)
            throw new AggregateException("Some files could not be restored.", errors);
    }

    private void Place(InstallContext context, string source, string target)
    {
        if (File.Exists(target))
        {
            var backup = BackupPathFor(context, target);
            FileSystemHelpers.MoveReplacing(target, backup);
            _journal.Add((Change.Replaced, target, backup));
        }
        else
        {
            _journal.Add((Change.Added, target, null));
        }
        FileSystemHelpers.MoveReplacing(source, target);
    }

    private static string BackupPathFor(InstallContext context, string target)
    {
        var relative = target.Substring(context.InstallDir.TrimEnd('\\').Length + 1);
        return Path.Combine(context.BackupDir, relative);
    }

    /// <summary>
    /// A config file counts as the user's when it differs from what Setup last installed. With no
    /// record of a previous install, any existing file is assumed to be the user's.
    /// </summary>
    internal static bool IsUserModified(string target, ManifestFile incoming, PayloadManifest? previous)
    {
        if (!File.Exists(target)) return false;

        var current = FileSystemHelpers.Sha256(target);
        if (string.Equals(current, incoming.Sha256, StringComparison.OrdinalIgnoreCase))
            return false;   // already identical to the new default

        var installed = previous?.Find(incoming.Path);
        return installed is null || !string.Equals(current, installed.Sha256, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Start menu shortcut, plus a desktop shortcut if the user asked for one.</summary>
internal sealed class ShortcutsStep : IInstallStep
{
    private readonly List<(string Path, string? Backup)> _changed = new();

    public string Title  => "Creating shortcuts";
    public double Weight => 2;

    public void Execute(InstallContext context, IProgress<double> progress, CancellationToken ct)
    {
        _changed.Clear();
        var paths = ShortcutPaths.For(context.Scope);

        Replace(context, paths.StartMenu, create: true);
        Replace(context, paths.Desktop, create: context.CreateDesktopShortcut);
    }

    public void Rollback(InstallContext context)
    {
        foreach (var (path, backup) in Enumerable.Reverse(_changed))
        {
            FileSystemHelpers.DeleteFileIfExists(path);
            if (backup is not null && File.Exists(backup))
                FileSystemHelpers.MoveReplacing(backup, path);
        }
        _changed.Clear();
    }

    private void Replace(InstallContext context, string path, bool create)
    {
        if (!create && !File.Exists(path)) return;

        string? backup = null;
        if (File.Exists(path))
        {
            backup = Path.Combine(context.BackupDir, "shortcuts", Guid.NewGuid().ToString("N") + ".lnk");
            FileSystemHelpers.MoveReplacing(path, backup);
        }
        _changed.Add((path, backup));

        if (create)
            ShellLink.Create(path, context.AppExePath, context.InstallDir,
                             "Monitor SQL Server and Azure SQL performance");
    }
}

/// <summary>Adds SqlVitals to Settings → Apps so it can be modified or uninstalled like any other app.</summary>
internal sealed class RegisterStep : IInstallStep
{
    private Dictionary<string, (object Value, Microsoft.Win32.RegistryValueKind Kind)>? _snapshot;
    private bool _written;

    public string Title  => "Registering SqlVitals with Windows";
    public double Weight => 1;

    public void Execute(InstallContext context, IProgress<double> progress, CancellationToken ct)
    {
        _snapshot = Registration.Snapshot(context.Scope);
        _written  = true;
        Registration.Write(context.Scope, context.InstallDir, context.Payload!.Manifest.Version,
                           context.Payload.Manifest.TotalSize, context.CreateDesktopShortcut);
    }

    public void Rollback(InstallContext context)
    {
        if (!_written) return;
        Registration.Restore(context.Scope, _snapshot);
        _written = false;
    }
}

/// <summary>Deletes the staging and backup folders. Leftovers are harmless, so failures are only logged.</summary>
internal sealed class CleanupStep : IInstallStep
{
    public string Title  => "Finishing up";
    public double Weight => 3;

    public void Execute(InstallContext context, IProgress<double> progress, CancellationToken ct)
    {
        foreach (var dir in new[] { context.StagingDir, context.BackupDir })
        {
            try
            {
                FileSystemHelpers.DeleteDirectoryIfExists(dir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                SetupLog.Warn($"Could not remove {dir}: {ex.Message}");
            }
        }
    }

    public void Rollback(InstallContext context) { }
}

// ── Uninstall ──────────────────────────────────────────────────────────────────

internal sealed class RemoveShortcutsStep : IInstallStep
{
    public string Title  => "Removing shortcuts";
    public double Weight => 1;

    public void Execute(InstallContext context, IProgress<double> progress, CancellationToken ct)
    {
        var paths = ShortcutPaths.For(context.Scope);
        FileSystemHelpers.DeleteFileIfExists(paths.StartMenu);
        FileSystemHelpers.DeleteFileIfExists(paths.Desktop);
    }

    public void Rollback(InstallContext context) { }
}

/// <summary>
/// Deletes only the files listed in the install manifest. Anything else in the folder was put
/// there by the user, so it is left alone and reported.
/// </summary>
internal sealed class RemoveFilesStep : IInstallStep
{
    public string Title  => "Removing application files";
    public double Weight => 20;

    public void Execute(InstallContext context, IProgress<double> progress, CancellationToken ct)
    {
        context.LeftBehind.Clear();
        if (!Directory.Exists(context.InstallDir)) return;

        var manifestPath = Path.Combine(context.InstallDir, ProductInfo.ManifestFileName);
        var manifest     = PayloadManifest.TryLoad(manifestPath);

        var toDelete = new List<string>();
        if (manifest is not null)
        {
            foreach (var file in manifest.Files)
            {
                var path = FileSystemHelpers.SafeCombine(context.InstallDir, file.Path);
                toDelete.Add(path);
                if (file.PreserveIfModified)
                    toDelete.Add(path + ".new");
            }
        }
        else
        {
            SetupLog.Warn("No install manifest found; only Setup's own files will be removed.");
        }

        toDelete.Add(Path.Combine(context.InstallDir, ProductInfo.SetupExeName));
        toDelete.Add(manifestPath);

        for (var i = 0; i < toDelete.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            FileSystemHelpers.DeleteFileIfExists(toDelete[i]);
            progress.Report((i + 1) / (double)toDelete.Count);
        }

        foreach (var dir in new[] { context.StagingDir, context.BackupDir })
            FileSystemHelpers.DeleteDirectoryIfExists(dir);

        FileSystemHelpers.DeleteEmptyDirectories(context.InstallDir, includeRoot: true);

        if (Directory.Exists(context.InstallDir))
            context.LeftBehind.Add(context.InstallDir);
    }

    public void Rollback(InstallContext context) { }
}

internal sealed class UnregisterStep : IInstallStep
{
    public string Title  => "Removing SqlVitals from the Windows app list";
    public double Weight => 1;

    public void Execute(InstallContext context, IProgress<double> progress, CancellationToken ct) =>
        Registration.Delete(context.Scope);

    public void Rollback(InstallContext context) { }
}

/// <summary>Only when the user ticked "Also delete my saved connections".</summary>
internal sealed class RemoveUserDataStep : IInstallStep
{
    public string Title  => "Removing saved connections";
    public double Weight => 1;

    public void Execute(InstallContext context, IProgress<double> progress, CancellationToken ct)
    {
        if (context.RemoveUserData)
            FileSystemHelpers.DeleteDirectoryIfExists(ProductInfo.UserDataDir);
    }

    public void Rollback(InstallContext context) { }
}
