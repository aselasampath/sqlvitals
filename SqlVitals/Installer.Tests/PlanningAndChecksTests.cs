using System;
using System.IO;
using SqlVitals.Installer.Core;

namespace SqlVitals.Installer.Tests;

public class InstallPlannerTests
{
    [Theory]
    [InlineData(null,    "2.0.0", InstallMode.Install)]
    [InlineData("1.9.3", "2.0.0", InstallMode.Upgrade)]
    [InlineData("2.0",   "2.0.0", InstallMode.Repair)]
    [InlineData("2.0.0", "2.0.0", InstallMode.Repair)]
    [InlineData("2.1.0", "2.0.0", InstallMode.Downgrade)]
    public void DefaultMode_matches_the_installed_version(string? installed, string package, InstallMode expected)
    {
        Assert.Equal(expected, InstallPlanner.DefaultMode(installed is null ? null : new Version(installed), new Version(package)));
    }

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\SqlVitals", false, InstallScope.User)]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\SqlVitals", true,  InstallScope.User)]
    [InlineData(@"C:\Program Files\SqlVitals",                   true,  InstallScope.Machine)]
    [InlineData(@"D:\Tools\SqlVitals",                           false, InstallScope.User)]
    public void ScopeFor_uses_machine_scope_only_outside_the_profile_with_admin_rights(string dir, bool elevated, InstallScope expected)
    {
        Assert.Equal(expected, InstallPlanner.ScopeFor(dir, elevated, @"C:\Users\me"));
    }
}

public class LocationValidatorTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"\\server\share\SqlVitals")]
    [InlineData(@"relative\SqlVitals")]
    [InlineData(@"C:")]
    [InlineData(@"C:\")]
    [InlineData(@"C:\bad|name")]
    public void CheckPath_rejects_unusable_folders(string input)
    {
        Assert.NotNull(LocationValidator.CheckPath(input, out _));
    }

    [Fact]
    public void CheckPath_rejects_the_Windows_folder()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        Assert.NotNull(LocationValidator.CheckPath(Path.Combine(windows, "SqlVitals"), out _));
    }

    [Theory]
    [InlineData(@"D:\Tools\SqlVitals",    @"D:\Tools\SqlVitals")]
    [InlineData(@"  D:\Tools\SqlVitals\ ", @"D:\Tools\SqlVitals")]
    [InlineData("\"D:\\Tools\\SqlVitals\"", @"D:\Tools\SqlVitals")]
    [InlineData(@"D:/Tools/SqlVitals",    @"D:\Tools\SqlVitals")]
    public void CheckPath_accepts_and_normalises_full_paths(string input, string expected)
    {
        Assert.Null(LocationValidator.CheckPath(input, out var full));
        Assert.Equal(expected, full);
    }

    [Theory]
    [InlineData(@"D:\Tools",            @"D:\Tools\SqlVitals")]
    [InlineData(@"D:\Tools\",           @"D:\Tools\SqlVitals")]
    [InlineData(@"D:\",                 @"D:\SqlVitals")]
    [InlineData(@"D:\Tools\sqlvitals",  @"D:\Tools\sqlvitals")]
    public void WithProductFolder_adds_a_SqlVitals_subfolder_once(string chosen, string expected)
    {
        Assert.Equal(expected, LocationValidator.WithProductFolder(chosen));
    }

    [Fact]
    public void IsOccupiedByOtherFiles_allows_empty_new_and_existing_SqlVitals_folders()
    {
        using var dir = new TempDir();
        Assert.False(LocationValidator.IsOccupiedByOtherFiles(dir.Path));                    // empty
        Assert.False(LocationValidator.IsOccupiedByOtherFiles(dir.File("does-not-exist")));

        File.WriteAllText(dir.File("someone-else.txt"), "x");
        Assert.True(LocationValidator.IsOccupiedByOtherFiles(dir.Path));

        File.WriteAllText(dir.File(ProductInfo.ManifestFileName), "x");
        Assert.False(LocationValidator.IsOccupiedByOtherFiles(dir.Path));                    // a previous SqlVitals install
    }
}

public class SystemRequirementsTests
{
    [Theory]
    [InlineData(10, 0, 22631, CheckStatus.Passed)]   // Windows 11
    [InlineData(10, 0, 19045, CheckStatus.Passed)]   // Windows 10 22H2
    [InlineData(10, 0, 14393, CheckStatus.Passed)]   // Windows 10 1607 (minimum)
    [InlineData(10, 0, 10240, CheckStatus.Failed)]   // Windows 10 RTM
    [InlineData(6,  3, 9600,  CheckStatus.Failed)]   // Windows 8.1
    public void CheckWindowsVersion(int major, int minor, int build, CheckStatus expected)
    {
        var result = SystemRequirements.CheckWindowsVersion(new Version(major, minor, build));
        Assert.Equal(expected, result.Status);
        if (expected == CheckStatus.Failed)
            Assert.False(string.IsNullOrEmpty(result.Resolution));
    }

    [Fact]
    public void CheckArchitecture_fails_on_32_bit_with_a_resolution()
    {
        Assert.Equal(CheckStatus.Passed, SystemRequirements.CheckArchitecture(true).Status);
        var failed = SystemRequirements.CheckArchitecture(false);
        Assert.Equal(CheckStatus.Failed, failed.Status);
        Assert.NotNull(failed.Resolution);
    }

    [Fact]
    public void CheckDiskSpace_says_how_much_to_free()
    {
        const long mb = 1024 * 1024;
        Assert.Equal(CheckStatus.Passed, SystemRequirements.CheckDiskSpace(@"C:\x", 100 * mb, 500 * mb).Status);

        var failed = SystemRequirements.CheckDiskSpace(@"C:\x", 300 * mb, 100 * mb);
        Assert.Equal(CheckStatus.Failed, failed.Status);
        Assert.Contains("200 MB", failed.Resolution);

        Assert.Equal(CheckStatus.Warning, SystemRequirements.CheckDiskSpace(@"C:\x", 100 * mb, null).Status);
    }

    [Fact]
    public void CheckPermissions_distinguishes_fresh_installs_from_admin_installs()
    {
        // Fresh install: only a warning, the user can pick a folder on the next screen.
        var fresh = SystemRequirements.CheckPermissions(@"C:\Program Files\SqlVitals", false, new RequirementInput());
        Assert.Equal(CheckStatus.Warning, fresh.Status);
        Assert.True(fresh.NeedsElevation);

        // All-users install changed by a standard user: must restart as admin.
        var machine = SystemRequirements.CheckPermissions(@"C:\Program Files\SqlVitals", false, new RequirementInput
        {
            Existing = new ExistingInstallation(new Version(1, 0), @"C:\Program Files\SqlVitals", InstallScope.Machine, false),
        });
        Assert.Equal(CheckStatus.Failed, machine.Status);
        Assert.True(machine.NeedsElevation);

        Assert.Equal(CheckStatus.Passed, SystemRequirements.CheckPermissions(@"C:\x", true, new RequirementInput()).Status);
    }

    [Fact]
    public void CheckNotRunning_blocks_while_the_app_is_open()
    {
        Assert.Equal(CheckStatus.Passed, SystemRequirements.CheckNotRunning(Array.Empty<int>()).Status);
        Assert.Equal(CheckStatus.Failed, SystemRequirements.CheckNotRunning(new[] { 1234 }).Status);
    }

    [Fact]
    public void Package_and_runtime_checks_need_a_complete_self_contained_payload()
    {
        const string h = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var complete = PayloadManifest.Parse(
            $"version\t2.0.0\nfile\t{h}\t1\t-\thostfxr.dll\nfile\t{h}\t1\t-\tcoreclr.dll\nfile\t{h}\t1\t-\twpfgfx_cor3.dll\n");
        var frameworkDependent = PayloadManifest.Parse($"version\t2.0.0\nfile\t{h}\t1\t-\tSqlVitals.Desktop.dll\n");

        Assert.Equal(CheckStatus.Passed, SystemRequirements.CheckPackage(complete, null).Status);
        Assert.Equal(CheckStatus.Failed, SystemRequirements.CheckPackage(null, "damaged").Status);
        Assert.Equal(CheckStatus.Passed, SystemRequirements.CheckRuntime(complete).Status);
        Assert.Equal(CheckStatus.Failed, SystemRequirements.CheckRuntime(frameworkDependent).Status);
    }

    [Fact]
    public void CanContinue_allows_warnings_but_not_failures()
    {
        var ok   = new RequirementResult("a", CheckStatus.Passed, "");
        var warn = new RequirementResult("b", CheckStatus.Warning, "");
        var fail = new RequirementResult("c", CheckStatus.Failed, "");

        Assert.True(SystemRequirements.CanContinue(new[] { ok, warn }));
        Assert.False(SystemRequirements.CanContinue(new[] { ok, fail }));
    }
}
