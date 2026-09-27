using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace SqlVitals.Engine.Security;

/// <summary>
/// A login that is a member of sysadmin, or holds CONTROL SERVER, from sys.server_role_members,
/// sys.server_permissions and sys.server_principals.
/// </summary>
/// <param name="Type">sys.server_principals.type: S SQL login, U Windows login, G Windows group,
/// E / X Microsoft Entra user / group, C certificate, K asymmetric key, R server role.</param>
/// <param name="Sid">The login's SID: a Windows SID for U and G, which names well-known accounts in any language.</param>
/// <param name="IsSa">The built-in sa login (SID 0x01), whatever it has been renamed to.</param>
/// <param name="PasswordPolicyChecked">SQL logins only: CHECK_POLICY. Null for the rest.</param>
/// <param name="ViaControlServer">Holds CONTROL SERVER rather than being a member of sysadmin.</param>
public sealed record SysadminMember(
    string    Name,
    string    Type,
    bool      IsDisabled,
    DateTime? CreateDate            = null,
    byte[]?   Sid                   = null,
    bool      IsSa                  = false,
    bool?     PasswordPolicyChecked = null,
    bool      ViaControlServer      = false)
{
    public bool IsSqlLogin => Type == "S";

    public bool IsGroup => Type is "G" or "X";

    /// <summary>"S-1-5-32-544" for a Windows login or group; null for the rest.</summary>
    public string? WindowsSid => Type is "U" or "G" && Sid is not null ? SidText.Format(Sid) : null;

    public string TypeText => Type switch
    {
        "S" => "SQL login",
        "U" => "Windows login",
        "G" => "Windows group",
        "E" => "Microsoft Entra login",
        "X" => "Microsoft Entra group",
        "C" => "Certificate login",
        "K" => "Asymmetric key login",
        "R" => "Server role",
        _   => Type,
    };
}

/// <summary>The built-in sa login, found by its SID (0x01), so also when renamed.</summary>
public sealed record SaLogin(string Name, bool IsDisabled)
{
    public bool IsRenamed => !Name.Equals("sa", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A database's TRUSTWORTHY setting and owner, from sys.databases.</summary>
/// <param name="Owner">SUSER_SNAME(owner_sid): null when the owner isn't a login on this server.</param>
/// <param name="OwnerIsSysadmin">IS_SRVROLEMEMBER('sysadmin') for the owner; null when it couldn't be told.</param>
public sealed record DatabaseTrust(string Name, bool IsTrustworthy, string? Owner, bool? OwnerIsSysadmin);

/// <summary>A SQL user whose SID matches no login on this server.</summary>
/// <param name="OwnedSchemas">Schemas it owns: DROP USER fails until they have another owner.</param>
public sealed record OrphanedUser(string Database, string Name, byte[] Sid, DateTime? CreateDate = null, int OwnedSchemas = 0)
{
    /// <summary>"0x5E1B…", for CREATE LOGIN … SID =.</summary>
    public string SidHex => "0x" + Convert.ToHexString(Sid);
}

/// <summary>A database the orphaned-user check couldn't look in, and why.</summary>
public sealed record SkippedDatabase(string Name, string Reason);

/// <summary>Where orphaned users were looked for, and what was found.</summary>
/// <param name="CheckedDatabases">The databases looked in, orphans or not.</param>
/// <param name="SqlLogins">The server's SQL logins, to offer remapping a user to the login of its name.</param>
public sealed record OrphanScan(
    IReadOnlyList<string>          CheckedDatabases,
    IReadOnlyList<SkippedDatabase> Skipped,
    IReadOnlyList<OrphanedUser>    Users,
    IReadOnlyCollection<string>    SqlLogins);

/// <summary>
/// What the security audit (#43) looks at, read in one go. Anything the login couldn't see is
/// null, and the checks that need it say so.
/// </summary>
/// <param name="Edition">SERVERPROPERTY('EngineEdition'): 5 is Azure SQL Database.</param>
/// <param name="WindowsAuthenticationOnly">SERVERPROPERTY('IsIntegratedSecurityOnly'): no SQL login can sign in.</param>
/// <param name="CanViewDefinitions">
/// VIEW ANY DEFINITION: without it, other logins, role members and database users are hidden, so
/// the lists would be incomplete and every user would look orphaned.
/// </param>
/// <param name="Sa">Null when it isn't visible.</param>
/// <param name="SysadminMembers">Null without <paramref name="CanViewDefinitions"/>.</param>
/// <param name="Databases">Every database except snapshots.</param>
/// <param name="Orphans">Null without <paramref name="CanViewDefinitions"/>.</param>
public sealed record SecuritySnapshot(
    int                             Edition,
    bool                            WindowsAuthenticationOnly,
    bool                            CanViewDefinitions,
    SaLogin?                        Sa,
    IReadOnlyList<SysadminMember>?  SysadminMembers,
    IReadOnlyList<DatabaseTrust>    Databases,
    OrphanScan?                     Orphans)
{
    public const int AzureSqlDatabaseEdition = 5;

    public bool IsAzureSqlDatabase => Edition == AzureSqlDatabaseEdition;
}

/// <summary>Windows SIDs in their "S-1-5-32-544" form.</summary>
public static class SidText
{
    /// <summary>"S-1-5-18" for the binary SID; null when it isn't one.</summary>
    public static string? Format(byte[] sid)
    {
        // Revision, the number of sub-authorities, a 6-byte big-endian authority, then each
        // sub-authority as a 4-byte little-endian number.
        if (sid.Length < 8 || sid[0] != 1 || sid.Length != 8 + sid[1] * 4)
            return null;

        long authority = 0;
        for (var i = 2; i < 8; i++)
            authority = (authority << 8) | sid[i];

        var sb = new StringBuilder("S-1-").Append(authority.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < sid[1]; i++)
            sb.Append('-').Append(BinaryPrimitives.ReadUInt32LittleEndian(sid.AsSpan(8 + i * 4, 4)).ToString(CultureInfo.InvariantCulture));
        return sb.ToString();
    }
}
