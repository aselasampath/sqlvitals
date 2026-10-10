using System.Security.AccessControl;
using System.Security.Principal;
using SqlVitals.Engine.Service;

namespace SqlVitals.Collector.Tests;

/// <summary>Install and uninstall (#172): the sc.exe steps, the service's permissions, and the data folder's.</summary>
public sealed class ServiceSetupTests : IDisposable
{
    private readonly TempFolder   _folder  = new();
    private readonly FakeWindows  _windows = new();
    private readonly StringWriter _output  = new();

    public void Dispose() => _folder.Dispose();

    private ServiceSetup Setup() => new(_windows, _output);

    private string[] Call(string verb) => Assert.Single(_windows.ScCalls, c => c[0] == verb);

    [Fact]
    public void Install_CreatesAnAutomaticService_AsItsVirtualAccount_ThatRestartsAfterACrash()
    {
        Assert.Equal(0, Setup().Install(_folder.Path, customDataDirectory: false, account: null, password: null, start: false));

        Assert.Equal(["create", "SqlVitalsCollector", "binPath=", "\"C:\\Program Files\\SqlVitals Collector\\SqlVitals.Collector.exe\" run",
                      "start=", "delayed-auto", "obj=", @"NT SERVICE\SqlVitalsCollector", "DisplayName=", "SqlVitals Collector"],
                     Call("create"));
        Assert.Equal(["failure", "SqlVitalsCollector", "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/300000"], Call("failure"));
        Assert.Contains(_windows.Calls, c => c == "create source SqlVitals Collector");
        Assert.Contains(_windows.Calls, c => c == $"lock {_folder.Path}");
        Assert.DoesNotContain(_windows.Calls, c => c.StartsWith("start", StringComparison.Ordinal));
        Assert.Contains("sc start SqlVitalsCollector", _output.ToString());
        Assert.Contains("GRANT VIEW SERVER STATE", _output.ToString());
        Assert.Contains($@"{Environment.MachineName}$", _output.ToString());
    }

    [Fact]
    public void Install_LetsTheInstallingUser_StartAndStopIt()
    {
        Setup().Install(_folder.Path, false, null, null, start: true);

        var me = _windows.CurrentUser.User!.Value;
        Assert.Equal(["sdset", "SqlVitalsCollector",
                      $"D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCLCSWLOCRRC;;;IU)(A;;{ServiceSetup.StartStopRights};;;{me})S:(AU;FA;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;WD)"],
                     Call("sdset"));
        Assert.Equal(["S-1-5-80-1111-2222", me], _windows.LockedFor!.Select(s => s.Value));
        Assert.Contains("start SqlVitalsCollector", _windows.Calls);
    }

    [Fact]
    public void Install_WithAnotherDataFolder_PassesItToTheService()
    {
        Setup().Install(@"D:\Vitals", customDataDirectory: true, null, null, false);

        Assert.Equal("\"C:\\Program Files\\SqlVitals Collector\\SqlVitals.Collector.exe\" run --data-dir \"D:\\Vitals\"", Call("create")[3]);
    }

    [Theory]
    [InlineData(@"CONTOSO\svc-vitals$", null)]
    [InlineData(@"NT AUTHORITY\NetworkService", null)]
    [InlineData(@"CONTOSO\svc-vitals", "s3cret")]
    public void Install_AsAnotherAccount_NeedsAPasswordOnlyWhenWindowsDoesntKeepOne(string account, string? password)
    {
        Setup().Install(_folder.Path, false, account, password, false);

        var create = Call("create");
        Assert.Equal(account, create[Array.IndexOf(create, "obj=") + 1]);
        Assert.Equal(password is not null, create.Contains("password="));
    }

    [Fact]
    public void Install_AsAUserAccount_WithoutItsPassword_IsRefused()
    {
        var ex = Assert.Throws<UsageException>(() => Setup().Install(_folder.Path, false, @"CONTOSO\svc-vitals", null, false));

        Assert.Contains("--password-stdin", ex.Message);
        Assert.Empty(_windows.ScCalls);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("profile")]
    [InlineData("installed")]
    public void Install_Refuses_WhenItCantWork(string why)
    {
        switch (why)
        {
            case "admin":     _windows.IsAdministrator = false; break;
            case "profile":   _windows.ExePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SqlVitals", "SqlVitals.Collector.exe"); break;
            case "installed": _windows.Installed = true; break;
        }

        var ex = Assert.Throws<UsageException>(() => Setup().Install(_folder.Path, false, null, null, false));

        Assert.Contains(why switch { "admin" => "administrator", "profile" => "user's folder", _ => "already installed" }, ex.Message);
        Assert.Empty(_windows.ScCalls);
    }

    [Fact]
    public void Install_ThatFailsHalfWay_RemovesTheService()
    {
        _windows.Answer = args => args[0] == "sdset" ? (5, "[SC] SetServiceObjectSecurity FAILED 5: Access is denied.") : null;

        var ex = Assert.Throws<UsageException>(() => Setup().Install(_folder.Path, false, null, null, false));

        Assert.Contains("Access is denied", ex.Message);
        Assert.Equal("delete", _windows.ScCalls[^1][0]);
    }

    [Fact]
    public void Uninstall_StopsAndRemovesIt_AndKeepsTheData_UnlessAsked()
    {
        _windows.Installed = true;
        File.WriteAllText(Path.Combine(_folder.Path, "collector.json"), "{}");

        Setup().Uninstall(_folder.Path, deleteData: false);

        Assert.Equal(["stop SqlVitalsCollector", "delete source SqlVitals Collector"], _windows.Calls);
        Assert.Equal(["delete", "SqlVitalsCollector"], Call("delete"));
        Assert.True(Directory.Exists(_folder.Path));
        Assert.Contains("--delete-data", _output.ToString());

        Setup().Uninstall(_folder.Path, deleteData: true);
        Assert.False(Directory.Exists(_folder.Path));
    }

    [Fact]
    public void Uninstall_OfAServiceThatWontStop_RemovesNothing()
    {
        _windows.Installed = true;
        _windows.Stops     = false;

        Assert.Throws<UsageException>(() => Setup().Uninstall(_folder.Path, deleteData: true));

        Assert.Empty(_windows.ScCalls);
        Assert.True(Directory.Exists(_folder.Path));
    }

    [Theory]
    [InlineData("D:(A;;CC;;;SY)S:(AU;FA;CC;;;WD)", "D:(A;;CC;;;SY)(A;;RPWP;;;S-1-5-21-1)S:(AU;FA;CC;;;WD)")]
    [InlineData("D:(A;;CC;;;SY)", "D:(A;;CC;;;SY)(A;;RPWP;;;S-1-5-21-1)")]
    [InlineData("D:(A;;CC;;;SY)(A;;LC;;;S-1-5-21-1)", "D:(A;;CC;;;SY)(A;;LC;;;S-1-5-21-1)")]
    public void ServicePermissions_GainOneEntry_ForTheUser(string sddl, string expected) =>
        Assert.Equal(expected, ServiceSecurity.Grant(sddl, "S-1-5-21-1", "RPWP"));

    [Fact]
    public void TheDataFolder_IsLockedToSystemTheAdminsAndWhoItsGivenTo()
    {
        var dir  = Path.Combine(_folder.Path, "Collector");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "collector.json"), "{}");
        var me   = WindowsIdentity.GetCurrent().User!;

        DataFolder.Lock(dir, [me]);

        var rules = new DirectoryInfo(dir).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                                          .Cast<FileSystemAccessRule>().ToList();
        Assert.All(rules, r => Assert.False(r.IsInherited));
        Assert.Equal(new[] { "S-1-5-18", "S-1-5-32-544", me.Value }.Order(StringComparer.Ordinal),
                     rules.Select(r => r.IdentityReference.Value).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(rules, r => r.IdentityReference.Value == "S-1-5-32-545");   // BUILTIN\Users
        Assert.Equal(FileSystemRights.Modify, rules.Single(r => r.IdentityReference == me).FileSystemRights & FileSystemRights.Modify);

        var file = new FileInfo(Path.Combine(dir, "collector.json")).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier))
                                                                    .Cast<FileSystemAccessRule>().ToList();
        Assert.All(file, r => Assert.True(r.IsInherited));
        Assert.DoesNotContain(file, r => r.IdentityReference.Value == "S-1-5-32-545");
    }

    [Fact]
    public void TheLoginScript_UsesThePcsAccount_InThePcsDomain_ForAVirtualOne()
    {
        Assert.Contains($@"CREATE LOGIN [CORP\{Environment.MachineName}$] FROM WINDOWS;", ServiceSetup.LoginScript(ServiceSetup.VirtualAccount, "CORP"));
        Assert.Contains(@"GRANT VIEW SERVER STATE TO [CONTOSO\svc$];", ServiceSetup.LoginScript(@"CONTOSO\svc$", "CORP"));

        var workgroup = ServiceSetup.LoginScript(ServiceSetup.VirtualAccount, machineDomain: null);
        Assert.Contains("isn't in a domain", workgroup);
        Assert.Contains(@"CREATE LOGIN [NT SERVICE\SqlVitalsCollector] FROM WINDOWS;", workgroup);
    }

    [Theory]
    [InlineData(@"C:\Program Files\x.exe", @"""C:\Program Files\x.exe""")]
    [InlineData(@"D:\Vitals\", @"""D:\Vitals\\""")]
    [InlineData(@"D:\", @"""D:\\""")]
    public void APath_IsQuoted_SoATrailingBackslashDoesntEscapeTheQuote(string path, string quoted)
    {
        Assert.Equal(quoted, ServiceSetup.Quoted(path));
        // As the service's own command line reads it back.
        Assert.Equal(path, CommandLineArgs($"x {quoted}")[1]);
    }

    private static string[] CommandLineArgs(string line)
    {
        var argv = CommandLineToArgvW(line, out var count);
        try
        {
            return Enumerable.Range(0, count).Select(i => System.Runtime.InteropServices.Marshal.PtrToStringUni(
                System.Runtime.InteropServices.Marshal.ReadIntPtr(argv, i * IntPtr.Size))!).ToArray();
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW(string line, out int count);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [Fact]
    public void AFolderSomethingElseMadeFirst_IsLockedByTheFirstCommandThatWritesIt()
    {
        var dir = Path.Combine(_folder.Path, "Collector");
        Directory.CreateDirectory(dir);   // as a console run or the history would, with inherited permissions
        Assert.False(new DirectoryInfo(dir).GetAccessControl().AreAccessRulesProtected);

        DataFolder.EnsureLocked(dir);

        Assert.True(new DirectoryInfo(dir).GetAccessControl().AreAccessRulesProtected);
    }
}
