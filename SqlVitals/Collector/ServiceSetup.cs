using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using SqlVitals.Engine.Service;

namespace SqlVitals.Collector;

/// <summary>What install and uninstall do to Windows: behind an interface, so tests check the steps without an admin prompt.</summary>
internal interface IWindowsSetup
{
    bool IsAdministrator { get; }

    /// <summary>This program's exe; null when unknown.</summary>
    string? ExePath { get; }

    /// <summary>Whether the service is installed.</summary>
    bool ServiceExists(string name);

    /// <summary>Runs sc.exe; its exit code and output.</summary>
    (int ExitCode, string Output) Sc(params string[] args);

    /// <summary>The SID of an account, e.g. NT SERVICE\SqlVitalsCollector; null when Windows doesn't know it.</summary>
    SecurityIdentifier? Sid(string account);

    /// <summary>The account running this command: the one install lets start and stop the service.</summary>
    WindowsIdentity CurrentUser { get; }

    /// <summary>The domain this PC is joined to (its NetBIOS name); null in a workgroup.</summary>
    string? MachineDomain { get; }

    void CreateEventSource(string source);
    void DeleteEventSource(string source);

    /// <summary>Gives the folder only these permissions (see <see cref="DataFolder.Lock"/>).</summary>
    void LockFolder(string directory, IReadOnlyCollection<SecurityIdentifier> modify);

    void StartService(string name);

    /// <summary>Stops the service and waits up to <paramref name="wait"/>; false when it didn't stop in time.</summary>
    bool StopService(string name, TimeSpan wait);
}

/// <summary>
/// Installs and uninstalls the collector as a Windows service (#172), with sc.exe:
/// <list type="bullet">
/// <item>runs as <c>NT SERVICE\SqlVitalsCollector</c> by default, a virtual account: no password,
/// nothing it can do on the PC beyond its own folder, and it reaches other servers as the PC's
/// domain account (DOMAIN\PC$). A gMSA (DOMAIN\name$), or an account with its password, instead;</item>
/// <item>starts automatically, delayed, and restarts after a crash;</item>
/// <item>lets the installing user start and stop it without an admin prompt, which the app
/// relies on (#172 part 2);</item>
/// <item>locks the data folder down to the service, the admins and that user.</item>
/// </list>
/// </summary>
internal sealed class ServiceSetup(IWindowsSetup windows, TextWriter output)
{
    /// <summary>The service's own virtual account.</summary>
    public const string VirtualAccount = $@"NT SERVICE\{CollectorPaths.ServiceName}";

    /// <summary>What the installing user may do to the service: query, start, stop, read its settings.</summary>
    internal const string StartStopRights = "CCLCSWRPWPLORC";

    private static readonly string[] BuiltInAccounts =
        ["LocalSystem", @"NT AUTHORITY\SYSTEM", @"NT AUTHORITY\NetworkService", @"NT AUTHORITY\LocalService"];

    /// <summary>Installs the service. Returns the exit code.</summary>
    /// <param name="password">The account's password; only for an account that isn't built in, virtual or a gMSA.</param>
    public int Install(string dataDirectory, bool customDataDirectory, string? account, string? password, bool start)
    {
        if (!windows.IsAdministrator)
            throw new UsageException("Installing a Windows service needs an administrator: run this from an elevated command prompt.");
        if (windows.ExePath is not { } exe)
            throw new UsageException("Couldn't tell where SqlVitals.Collector.exe is.");
        if (UnderAUserProfile(exe))
            throw new UsageException(
                $"SqlVitals.Collector.exe is in a user's folder ({Path.GetDirectoryName(exe)}), which the service's account can't read. " +
                @"Copy the folder somewhere every account can read, such as C:\Program Files\SqlVitals Collector, and install from there.");
        if (windows.ServiceExists(CollectorPaths.ServiceName))
            throw new UsageException(
                $"The {CollectorPaths.DisplayName} service is already installed. To update it, stop it, replace the files and start it; " +
                "to install it again, uninstall it first.");

        var logOn = string.IsNullOrWhiteSpace(account) ? VirtualAccount : account.Trim();
        var needsPassword = !IsPasswordless(logOn);
        if (needsPassword && string.IsNullOrEmpty(password))
            throw new UsageException(
                $"{logOn} needs its password: add --password-stdin and pipe it in. A gMSA (DOMAIN\\name$) or the default virtual account needs none.");

        var binPath = $"{Quoted(exe)} run" + (customDataDirectory ? $" --data-dir {Quoted(dataDirectory)}" : "");
        var create  = new List<string>
        {
            "create", CollectorPaths.ServiceName, "binPath=", binPath, "start=", "delayed-auto",
            "obj=", logOn, "DisplayName=", CollectorPaths.DisplayName,
        };
        if (needsPassword)
            create.AddRange(["password=", password!]);
        Run("Creating the service", [.. create]);

        try
        {
            Run("Describing it", "description", CollectorPaths.ServiceName,
                "Watches SQL Server around the clock for SqlVitals: samples, alerts, scores and chat notifications, " +
                "saved to its own history. Set up with SqlVitals.Collector.exe.");
            // Restarted a minute after a crash, twice, then after five minutes; the count resets after a day.
            Run("Setting it to restart after a failure", "failure", CollectorPaths.ServiceName,
                "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/300000");

            var user = windows.CurrentUser.User
                       ?? throw new UsageException("Couldn't tell which Windows user is running this.");
            var (shown, sddl) = windows.Sc("sdshow", CollectorPaths.ServiceName);
            if (shown != 0)
                throw new UsageException($"Couldn't read the service's permissions: {sddl.Trim()}");
            Run($"Letting {windows.CurrentUser.Name} start and stop it", "sdset", CollectorPaths.ServiceName,
                ServiceSecurity.Grant(sddl.Trim(), user.Value, StartStopRights));

            windows.CreateEventSource(CollectorPaths.DisplayName);

            var service = windows.Sid(logOn)
                          ?? throw new UsageException($"Windows doesn't know the account {logOn}.");
            windows.LockFolder(dataDirectory, [service, user]);
            output.WriteLine($"Locked {dataDirectory} to SYSTEM, Administrators, {logOn} and {windows.CurrentUser.Name}.");
        }
        catch
        {
            // Half an install is worse than none: the next install would say it's already there.
            windows.Sc("delete", CollectorPaths.ServiceName);
            throw;
        }

        output.WriteLine($"Installed the {CollectorPaths.DisplayName} service, running as {logOn}.");
        if (start)
        {
            windows.StartService(CollectorPaths.ServiceName);
            output.WriteLine("Started it.");
        }
        else
        {
            output.WriteLine($"Start it with: sc start {CollectorPaths.ServiceName}   (it also starts by itself after the next restart)");
        }
        output.WriteLine();
        output.WriteLine("Each server it watches needs a login for that account with VIEW SERVER STATE:");
        output.WriteLine(LoginScript(logOn, windows.MachineDomain));
        return 0;
    }

    /// <summary>Stops and removes the service. Its settings and history stay unless <paramref name="deleteData"/>.</summary>
    public int Uninstall(string dataDirectory, bool deleteData)
    {
        if (!windows.IsAdministrator)
            throw new UsageException("Removing a Windows service needs an administrator: run this from an elevated command prompt.");

        if (windows.ServiceExists(CollectorPaths.ServiceName))
        {
            if (!windows.StopService(CollectorPaths.ServiceName, TimeSpan.FromSeconds(30)))
                throw new UsageException($"The {CollectorPaths.DisplayName} service didn't stop within 30 seconds. Try again in a moment.");
            Run("Removing the service", "delete", CollectorPaths.ServiceName);
            windows.DeleteEventSource(CollectorPaths.DisplayName);
            output.WriteLine($"Removed the {CollectorPaths.DisplayName} service.");
        }
        else
        {
            output.WriteLine($"The {CollectorPaths.DisplayName} service isn't installed.");
        }

        if (deleteData)
        {
            if (Directory.Exists(dataDirectory))
                Directory.Delete(dataDirectory, recursive: true);
            output.WriteLine($"Deleted {dataDirectory}: its settings, history and logs.");
        }
        else if (Directory.Exists(dataDirectory))
        {
            output.WriteLine($"Kept {dataDirectory}: its settings and history. Delete them with: SqlVitals.Collector uninstall --delete-data");
        }
        return 0;
    }

    /// <summary>
    /// The T-SQL that lets the service's account read a server, for a DBA to run on each one. A
    /// virtual or built-in account reaches another server as the PC's own account, in the PC's
    /// domain (not the domain of whoever runs install); in a workgroup it has none.
    /// </summary>
    internal static string LoginScript(string account, string? machineDomain)
    {
        if (IsMachineAccount(account) && machineDomain is null)
            return $"""
                    This PC isn't in a domain, so {account} can't sign in to another server with Windows authentication.
                    Add those servers with --sql-login, or install with --account set to an account the servers know.
                    For a SQL Server on this PC itself: CREATE LOGIN [{LocalLogin(account)}] FROM WINDOWS; GRANT VIEW SERVER STATE TO [{LocalLogin(account)}];
                """;
        var login = IsMachineAccount(account) ? $@"{machineDomain}\{Environment.MachineName}$" : account;
        return $"""
                    CREATE LOGIN [{login}] FROM WINDOWS;
                    GRANT VIEW SERVER STATE TO [{login}];
                """ + (IsMachineAccount(account)
                    ? $"{Environment.NewLine}    (For a SQL Server on this PC itself, the login is [{LocalLogin(account)}].)"
                    : "");
    }

    private static string LocalLogin(string account) =>
        account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) ? @"NT AUTHORITY\SYSTEM" : account;

    /// <summary>
    /// A path in quotes as one argument of the service's command line. Backslashes before the
    /// closing quote are doubled, so D:\ stays D:\ rather than escaping the quote.
    /// </summary>
    internal static string Quoted(string path)
    {
        var trailing = path.Length - path.TrimEnd('\\').Length;
        return $"\"{path}{new string('\\', trailing)}\"";
    }

    private static bool IsMachineAccount(string account) =>
        account == VirtualAccount || BuiltInAccounts.Contains(account, StringComparer.OrdinalIgnoreCase);

    // A virtual account, a built-in one or a gMSA: Windows keeps its password.
    private static bool IsPasswordless(string account) =>
        IsMachineAccount(account) || account.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase) || account.EndsWith('$');

    private static bool UnderAUserProfile(string exe)
    {
        var users = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return users is not null && Path.GetFullPath(exe).StartsWith(users + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private void Run(string what, params string[] args)
    {
        var (exit, text) = windows.Sc(args);
        if (exit != 0)
            throw new UsageException($"{what} failed (sc.exe {exit}): {text.Trim()}");
    }
}

/// <summary>A service's permissions as SDDL, the form sc.exe sdshow and sdset use.</summary>
internal static class ServiceSecurity
{
    /// <summary>
    /// Adds an allow entry for <paramref name="sid"/> to the DACL, after its other entries and
    /// before the audit part (S:). Unchanged when the SID already has an entry.
    /// </summary>
    public static string Grant(string sddl, string sid, string rights)
    {
        var ace = $"(A;;{rights};;;{sid})";
        var dacl = sddl.IndexOf("D:", StringComparison.Ordinal);
        if (dacl < 0)
            throw new FormatException("The service's permissions have no DACL.");
        var sacl = sddl.IndexOf("S:", dacl, StringComparison.Ordinal);
        var end  = sacl < 0 ? sddl.Length : sacl;
        if (sddl[dacl..end].Contains($";;;{sid})", StringComparison.OrdinalIgnoreCase))
            return sddl;
        return sddl[..end] + ace + sddl[end..];
    }
}

/// <summary>
/// The data folder's permissions (#172). LocalMachine DPAPI lets any process on the PC decrypt the
/// settings, so these permissions are what keep the secrets from other users: full control for
/// SYSTEM and the Administrators, Modify for the service's account and the installing user, and
/// nothing inherited from %ProgramData%, which lets every user read.
/// </summary>
internal static class DataFolder
{
    private static readonly SecurityIdentifier System         = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public static void Lock(string directory, IReadOnlyCollection<SecurityIdentifier> modify)
    {
        var info = Directory.CreateDirectory(directory);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        foreach (var sid in modify.Distinct())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify, all, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);

        // Files already there (a settings file written before install) take the folder's entries.
        foreach (var file in info.EnumerateFiles("*", SearchOption.AllDirectories))
        {
            var own = new FileSecurity();
            own.SetAccessRuleProtection(isProtected: false, preserveInheritance: false);
            file.SetAccessControl(own);
        }
    }

    /// <summary>
    /// Locks the folder down to this user, the admins and SYSTEM, unless it already is (by
    /// install, or an earlier command): when it doesn't exist yet, and when something else made it
    /// with %ProgramData%'s permissions, such as a first try in the console. Install adds the
    /// service's account.
    /// </summary>
    public static void EnsureLocked(string directory)
    {
        if (Directory.Exists(directory) && new DirectoryInfo(directory).GetAccessControl().AreAccessRulesProtected)
            return;
        using var me = WindowsIdentity.GetCurrent();
        Lock(directory, me.User is { } sid ? [sid] : []);
    }
}

/// <summary>The real Windows behind <see cref="IWindowsSetup"/>.</summary>
internal sealed class WindowsSetup : IWindowsSetup
{
    public bool IsAdministrator
    {
        get
        {
            using var me = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(me).IsInRole(WindowsBuiltInRole.Administrator);
        }
    }

    public string? ExePath => Environment.ProcessPath;

    public WindowsIdentity CurrentUser { get; } = WindowsIdentity.GetCurrent();

    public string? MachineDomain
    {
        get
        {
            if (NetGetJoinInformation(null, out var name, out var status) != 0)
                return null;
            try
            {
                // 3: joined to a domain; 2 is a workgroup.
                return status == 3 ? Marshal.PtrToStringUni(name) : null;
            }
            finally
            {
                _ = NetApiBufferFree(name);
            }
        }
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NetGetJoinInformation(string? server, out IntPtr name, out int status);

    [DllImport("netapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int NetApiBufferFree(IntPtr buffer);

    public bool ServiceExists(string name) =>
        ServiceController.GetServices().Any(s => string.Equals(s.ServiceName, name, StringComparison.OrdinalIgnoreCase));

    public (int ExitCode, string Output) Sc(params string[] args)
    {
        var start = new ProcessStartInfo("sc.exe")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("sc.exe didn't start.");
        var text = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, text);
    }

    public SecurityIdentifier? Sid(string account)
    {
        try
        {
            var name = account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) ? @"NT AUTHORITY\SYSTEM" : account;
            return (SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier));
        }
        catch (IdentityNotMappedException)
        {
            return null;
        }
    }

    public void CreateEventSource(string source)
    {
        if (!EventLog.SourceExists(source))
            EventLog.CreateEventSource(source, "Application");
    }

    public void DeleteEventSource(string source)
    {
        if (EventLog.SourceExists(source))
            EventLog.DeleteEventSource(source);
    }

    public void LockFolder(string directory, IReadOnlyCollection<SecurityIdentifier> modify) => DataFolder.Lock(directory, modify);

    public void StartService(string name)
    {
        using var service = new ServiceController(name);
        service.Start();
    }

    public bool StopService(string name, TimeSpan wait)
    {
        using var service = new ServiceController(name);
        if (service.Status is ServiceControllerStatus.Stopped)
            return true;
        if (service.Status is not ServiceControllerStatus.StopPending)
            service.Stop();
        try
        {
            service.WaitForStatus(ServiceControllerStatus.Stopped, wait);
            return true;
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            return false;
        }
    }
}
