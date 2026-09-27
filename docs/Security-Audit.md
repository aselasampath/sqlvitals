# Security Audit

> **Where:** sidebar → 🔒 Security Audit · **Use it when:** you take over a server, before a security review, or after a database was restored from another server, to see who holds the keys and what was left open · **Needs:** `VIEW ANY DEFINITION` to list the sysadmin members and the orphaned users; the sa login and TRUSTWORTHY need nothing extra (see [Server Impact and Permissions](Server-Impact-and-Permissions.md)) · **Not on Azure SQL Database** · **New in 0.44.0**

A few settings account for much of a SQL Server's attack surface: who is **sysadmin**, whether the built-in **sa** login can sign in, databases marked **TRUSTWORTHY**, and **orphaned users** left behind by restores and dropped logins. This page audits all four and says, for every finding, whether it's a **Risk**, something to **Review**, or a **Pass**, and **what the risk is**.

Everything is read from the catalog views. SqlVitals changes nothing: for a risk it shows the T-SQL that would remove it, for you to review and run yourself.

---

## How to use it

1. Open **🔒 Security Audit**. Risks come first, in red, then what to review, what couldn't be checked, and what passes.
2. The line under the toolbar counts them: *3 risks · 4 to review · 6 passed · 1 not checked*. An amber line under it says when the login can't see everything.
3. **Show** narrows the list: *Risks*, *To review*, one check (*sysadmin members*, *sa login*, *TRUSTWORTHY*, *Orphaned users*) or *Not checked*. **↻ Audit again** (or the sidebar **Refresh**) reads everything again, after you've fixed something, say.
4. Each row is one finding:

   | Column | Meaning |
   |---|---|
   | **Status** | **Risk** (red): widens the attack surface, fix it unless there's a reason not to. **Review** (amber): not wrong in itself, but someone should know why it's there. **Pass** (green). **Not checked** (grey): a permission is missing, or a database couldn't be read |
   | **Check** | *sa login*, *sysadmin member*, *TRUSTWORTHY* or *Orphaned user* |
   | **Scope** | **Server** for a login; otherwise the database. Databases that pass are one row: *All 24 databases*, or *21 other databases* |
   | **Login, database or user** | What the finding is about |
   | **Finding** | What was found: *SQL login · enabled*, *ON · owner sa*, *SQL user, no login · created 2019-04-02* |
   | **Risk** | Why it matters, starting with what is specific to this row |

5. **Select a finding** to read the whole explanation below and, for a risk, the **script to remove it**. **⧉ Copy** copies it.
6. **📝 Script the risks** puts the fix for every risk into one script, to review, save and run yourself.

The grid has the usual filter box and right-click menu (copy, export to CSV). See [Working with Grids](Working-with-Grids.md).

---

## What is audited

### The sa login

sa is SQL Server's built-in administrator: always sysadmin, can't be dropped, and its name is known to everyone, so password-guessing tries it first. It is found by its SID (`0x01`), so a renamed sa is still found.

| Finding | Status |
|---|---|
| Enabled, and the server accepts SQL Server logins (mixed mode) | **Risk**, renamed or not: renaming hides it from guessing by name, but it still signs in with a password |
| Enabled, but the server accepts Windows logins only | **Review**: it can't sign in now, but can the moment mixed mode is turned on |
| Disabled | **Pass** |

The fix is `ALTER LOGIN [sa] DISABLE;`. First check that nothing signs in as sa (an application, a linked server, a job step's connection) and that another sysadmin login works.

### sysadmin members

Every member of the **sysadmin** role is listed, and so is every login or role granted **CONTROL SERVER**, which gives almost everything sysadmin does. A login that is both is listed once.

| Member | Status | Why |
|---|---|---|
| SQL Server's own service accounts: `NT SERVICE\MSSQLSERVER` (`MSSQL$<instance>`), `SQLSERVERAGENT` (`SQLAgent$<instance>`), `SQLWriter`, `Winmgmt` | **Pass** | Setup makes them sysadmin; the Database Engine, Agent, VSS backups and WMI need it |
| `BUILTIN\Administrators`, `NT AUTHORITY\SYSTEM`, *Authenticated Users*, `BUILTIN\Users`, *Everyone*, a domain's *Domain Users* or *Domain Computers* | **Risk** | Everyone in them is sysadmin: every local administrator, every service running as LocalSystem, every user of the domain. Recognised by their Windows SID, so also on a German or French Windows |
| An enabled SQL login other than sa | **Risk** | It signs in with a password, which can be guessed, reused elsewhere or read from an application's connection string. The explanation adds when `CHECK_POLICY` is OFF: no complexity rules and no lockout |
| A Windows or Microsoft Entra group | **Review** | Everyone in the group is sysadmin, and who is in it is managed outside SQL Server |
| A Windows login, certificate or asymmetric key login, or a server role with CONTROL SERVER | **Review** | Check it still needs it |
| sa | **Review** (Pass when disabled) | See the sa login check above |
| Any of the risks above, but disabled, or a SQL login on a server that accepts Windows logins only | **Review** | It can't sign in now, but anyone who can alter logins can enable it again |

The fix is `ALTER SERVER ROLE [sysadmin] DROP MEMBER [login];` (`REVOKE CONTROL SERVER FROM [login] CASCADE;` for CONTROL SERVER). Grant it the permissions it actually uses first, or whatever depends on it stops working.

### TRUSTWORTHY databases

`TRUSTWORTHY ON` tells SQL Server to trust a database's code outside the database: a procedure in it that uses `EXECUTE AS` can act on the server with the permissions of the database's **owner**.

| Database | Status | Why |
|---|---|---|
| ON, and its owner is sysadmin (sa, say) | **Risk** | Anyone who is db_owner in it, or can create a procedure in it, can make themselves sysadmin |
| ON, owner not sysadmin, or not a login on this server | **Risk** | The code gets the owner's permissions. Few databases need it: sign the code that does with a certificate |
| msdb ON | **Pass** | msdb ships TRUSTWORTHY ON, and SQL Server Agent and Database Mail rely on it |
| OFF | **Pass** | One row for all of them |

The fix is `ALTER DATABASE [db] SET TRUSTWORTHY OFF;`. First check what in it needs the setting: CLR assemblies marked `EXTERNAL_ACCESS` or `UNSAFE`, and code that uses `EXECUTE AS` to reach other databases or the server.

### Orphaned users

An **orphaned user** is a SQL user whose SID matches no login on this server, usually because the database was restored or attached from another server, or its login was dropped. Whatever signs in as it can't reach the database, and its permissions stay in place for whoever next creates a login with its SID, or remaps it with `ALTER USER … WITH LOGIN`. Each one is a **Risk**.

Only SQL users made for a login count. Users without a login, contained users with a password, Windows users (who can get in through a Windows group) and `dbo`, `guest`, `INFORMATION_SCHEMA` and `sys` are not orphans.

| Finding | Fix |
|---|---|
| *SQL user · login app exists*: a SQL login of the same name exists, with a different SID | `ALTER USER [app] WITH LOGIN = [app];` in the database: the user gets the login's SID |
| *SQL user, no login* | Two choices, both commented out until you pick one: create the login with the user's SID (`CREATE LOGIN … SID = 0x…`), so the two match; or, if nobody needs it, `DROP USER`, after giving any schemas it owns another owner |

Databases that aren't online, that the login can't open (an availability group secondary that isn't readable, say), or whose users couldn't be read are listed as **Not checked**, one row each, with the reason.

---

## Reading it

| What you see | Usually means | Try |
|---|---|---|
| **sa** enabled | The server was installed in mixed mode and sa kept for convenience or an old application | Find what uses it (`sys.dm_exec_sessions` where `login_name = 'sa'`, the application's connection strings), give it its own login, then disable sa |
| **BUILTIN\Administrators** is sysadmin | An upgrade from SQL Server 2005 or earlier, or it was added back by hand | Add a Windows group of the DBAs as sysadmin, check it works, then drop the local group |
| An application's **SQL login** is sysadmin | Easier than working out what it needs | Give it `db_datareader`, `db_datawriter` and `EXECUTE` in its own databases, or what it actually uses, then drop it from sysadmin |
| A **TRUSTWORTHY** database owned by **sa** | A CLR assembly or cross-database code needed it once | Sign the assembly or module with a certificate, then turn it off. Changing the owner to a login with no other permissions lowers the risk meanwhile |
| **Orphaned users** in a database restored from another server | Its logins were never created here | Copy the logins with their SIDs from the source server (Microsoft's `sp_help_revlogin`), and the users match again |
| **Orphaned users** in an availability group database on the secondary | Logins created on the primary only | Create the same logins, with the same SIDs, on every replica before a failover |

### Things to know

- **Direct membership.** A Windows login that is sysadmin only through a Windows group is not listed on its own: the group is. SqlVitals can't see who is in a Windows group; that's for Active Directory.
- **Only these four.** It's a quick audit, not a full security baseline: it doesn't check password strength, permissions granted to `public`, `xp_cmdshell`, encryption or auditing.
- **Snapshots** are left out: they take their users and settings from their source database.

---

## Azure SQL

| | SQL Server | Azure SQL Managed Instance | Azure SQL Database |
|---|---|---|---|
| sa login | ✅ | ✅ | ➖ |
| sysadmin members and CONTROL SERVER | ✅ | ✅ (Microsoft Entra logins and groups too) | ➖ |
| TRUSTWORTHY | ✅ | ✅ | ➖ |
| Orphaned users | ✅ Every database | ✅ Every database | ➖ |

Azure SQL Database has no sysadmin role, no sa login and no TRUSTWORTHY setting, and its logins are in the logical server's `master`, which a user database can't see. The **🔒 Security Audit** button is disabled on it.

---

## When it can't check everything

| Message | Why | What to do |
|---|---|---|
| *This login can't see the server's other logins or the users in each database* | Without `VIEW ANY DEFINITION` a login sees only itself and a few system principals, so the list of sysadmin members would be incomplete and every user would look orphaned | `GRANT VIEW ANY DEFINITION TO [login];` It shows the definitions of logins, users and roles, but no data. Meanwhile sa and TRUSTWORTHY are still checked |
| An orphaned-user row *Not checked*, *it is OFFLINE* or *RESTORING* | The database can't be read | Nothing to do until it's online |
| *this login can't open it* | The login has no user in that database, or it's an availability group secondary that isn't readable | Check it on the primary, or grant the login access |

---

### Under the hood

- `SecurityAuditRepository.GetSecurityAuditAsync` reads `SERVERPROPERTY('EngineEdition')` and `IsIntegratedSecurityOnly`, and `HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW ANY DEFINITION')`; then:
  - the sa login from `sys.server_principals` by `sid = 0x01`;
  - `sys.databases` (snapshots left out): `is_trustworthy_on`, the owner with `SUSER_SNAME(owner_sid)`, and `IS_SRVROLEMEMBER('sysadmin', owner)` (only when the owner has a name: given NULL it answers for the caller);
  - with `VIEW ANY DEFINITION`: the members of sysadmin from `sys.server_role_members`, and the grants of CONTROL SERVER from `sys.server_permissions` (granted, not denied), with `sys.sql_logins.is_policy_checked`; and, in each online database the login can open, one query of `<db>.sys.database_principals` for SQL users (`authentication_type = 1`) whose SID isn't in `sys.server_principals`, with the schemas each owns. A database whose query fails is reported on its own; the rest are still checked.
- `Engine/Security/SecurityAudit.Run` (pure, unit tested) turns what was read into the rows: the statuses, the explanations and the fix scripts. Windows' well-known accounts are recognised by SID (`S-1-5-32-544` and so on), not by name.
- It runs when the page is opened or refreshed: light catalog queries, one per database for orphaned users, never in the background. See [Server Impact and Permissions](Server-Impact-and-Permissions.md).
