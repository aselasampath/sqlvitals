# Security and Privacy

What SqlVitals stores, where, how it's protected, and what leaves your PC.

## At a glance

- **No telemetry, no cloud.** SqlVitals makes no network connections except to the SQL Servers you configure.
- **Credentials are encrypted** with Windows DPAPI, per Windows user.
- **Passwords are optional on disk.** Untick *Remember password* and they stay in memory for the session only.
- **Logs are scrubbed** of passwords and connection strings.
- **History is local and unencrypted.** It can include query text. See below.

## What is stored, and where

| Data | Location | Protection | Contains |
|---|---|---|---|
| Saved connections and settings | `%AppData%\SqlVitals\settings.dat` | **DPAPI** (current user) | Server, database, auth type, login, password (only if *Remember password*), thresholds, preferences |
| Monitoring history | `%LocalAppData%\SqlVitals\history.db` | Windows file permissions (your profile) | Metrics, waits, file I/O, and **query text** (up to 4,000 characters per query hash) |
| Diagnostic logs | `%AppData%\SqlVitals\logs\SqlVitals-yyyyMMdd.log` | Windows file permissions | Errors and warnings; **passwords and connection strings removed**; 14-day retention |
| Exported reports / CSVs / scripts | Wherever you save them | Your responsibility | Whatever you exported |
| Setup log | `%TEMP%\SqlVitals-Setup-*.log` | — | Install steps and paths only |

## Credentials

- `settings.dat` is encrypted with the Windows **Data Protection API** in *CurrentUser* scope. Only your Windows account on this PC can decrypt it. Copying the file to another PC or user doesn't reveal it.
- Credentials never appear in the connection list, the connection selector or error messages.
- The *Additional parameters* field rejects `Password` and `User ID`, so a secret can't be stored outside the encrypted fields.
- **Entra MFA** uses the interactive Microsoft sign-in. SqlVitals never sees your Microsoft password.
- **Windows Authentication** stores no secret.

## Connections to your servers

- Encryption defaults to **Mandatory**. **Strict** (TDS 8.0) is available for SQL Server 2022 and Azure SQL.
- **Trust server certificate** is off by default. Turn it on only for servers with self-signed certificates on networks you trust.
- Use a **dedicated low-privilege login** with just `VIEW SERVER STATE` (or `VIEW DATABASE STATE`). See [Server Impact and Permissions](Server-Impact-and-Permissions.md#permissions).

## Query text and sensitive data

Query text captured from the server (in history, exports and copied grids) may contain **literal values**: customer names, IDs, amounts. Before sharing:

- **Export for AI reports**: read them before pasting into an external service.
- **`history.db`**: don't send it to others without checking. **Settings → Clear history** deletes it all.
- **Logs** are safe to attach to bug reports as far as credentials go, but may include server and database names.

## Installer

- Setup contains every file it installs and downloads nothing.
- Every file is verified against a SHA-256 manifest before being installed. Manifest paths are validated so nothing is written outside the install folder.
- Installs per-user without admin rights by default. Release builds can be code-signed.
- Uninstall leaves your data unless you tick the box to remove it.

## Reporting a vulnerability

Please open an issue at [github.com/aselasampath/sqlvitals/issues](https://github.com/aselasampath/sqlvitals/issues). For anything sensitive, ask for a private contact in the issue rather than posting details publicly.
