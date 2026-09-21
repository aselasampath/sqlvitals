using System;
using System.IO;
using SqlVitals.Installer.Core;

namespace SqlVitals.Installer;

/// <summary>Choices and facts gathered as the user moves through the wizard.</summary>
public sealed class SetupSession
{
    public SetupSession(string exePath, string[] args, Payload? payload, string? payloadError,
                        ExistingInstallation? existing, bool startWithUninstall)
    {
        ExePath            = exePath;
        Args               = args;
        Payload            = payload;
        PayloadError       = payloadError;
        Existing           = existing;
        StartWithUninstall = startWithUninstall;

        Mode = startWithUninstall ? InstallMode.Uninstall
             : InstallPlanner.DefaultMode(existing?.Version, PackageVersion ?? new Version(0, 0));

        InstallDir            = existing?.InstallDir ?? ProductInfo.DefaultInstallDir;
        CreateDesktopShortcut = existing?.DesktopShortcut ?? true;
    }

    public string                ExePath            { get; }
    public string[]              Args               { get; }
    public Payload?              Payload            { get; }
    public string?               PayloadError       { get; }
    public ExistingInstallation? Existing           { get; }
    public bool                  StartWithUninstall { get; }

    public InstallMode Mode                  { get; set; }
    public string      InstallDir            { get; set; }
    public bool        CreateDesktopShortcut { get; set; }
    public bool        RemoveUserData        { get; set; }

    public Version? PackageVersion => Payload?.Manifest.Version;

    public string PackageVersionText => PackageVersion?.ToString(3) ?? "(unknown)";

    /// <summary>Upgrades, repairs and uninstall act on the existing folder; only a fresh install can choose.</summary>
    public bool CanChooseLocation => Existing is null;

    public long SetupExeSize
    {
        get
        {
            try { return new FileInfo(ExePath).Length; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        }
    }

    public long RequiredBytes =>
        Payload is null ? 0 : InstallPlanner.RequiredBytes(Payload.Manifest, SetupExeSize);

    /// <summary>Registration scope: an existing install keeps its own; a new one depends on the folder.</summary>
    public InstallScope Scope =>
        Existing?.Scope ?? InstallPlanner.ScopeFor(InstallDir, Elevation.IsElevated,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public string ActionVerb => Mode switch
    {
        InstallMode.Upgrade   => "Upgrade",
        InstallMode.Repair    => "Repair",
        InstallMode.Downgrade => "Replace",
        InstallMode.Uninstall => "Uninstall",
        _                     => "Install",
    };

    public InstallContext CreateContext()
    {
        var context = new InstallContext(Mode, InstallDir, Scope)
        {
            Payload               = Payload,
            SetupExePath          = ExePath,
            CreateDesktopShortcut = CreateDesktopShortcut,
            RemoveUserData        = RemoveUserData,
        };

        if (Existing is not null)
            context.PreviousManifest = PayloadManifest.TryLoad(Path.Combine(Existing.InstallDir, ProductInfo.ManifestFileName));

        return context;
    }
}
