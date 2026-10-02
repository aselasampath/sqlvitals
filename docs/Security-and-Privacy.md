# Security and Privacy

What SqlVitals stores, where, how it's protected, and what leaves your PC.

## At a glance

- **No telemetry, no cloud.** SqlVitals makes no network connections except to the SQL Servers you configure.
- **Credentials are encrypted** with Windows DPAPI, per Windows user.
- **Passwords are optional on disk.** Untick *Remember password* and they stay in memory for the session only.
- **Logs are scrubbed** of passwords and connection strings.
- **History is local and unencrypted.** It can include query text. See below.
- **The activity log never holds a secret.** It records who changed what, but for a password or connection string only that it changed.

## What is stored, and where

| Data | Location | Protection | Contains |
|---|---|---|---|
| Saved connections and settings | `%AppData%\SqlVitals\settings.dat` | **DPAPI** (current user) | Server, database, auth type, login, password (only if *Remember password*), thresholds, preferences |
| Monitoring history | `%LocalAppData%\SqlVitals\history.db` | Windows file permissions (your profile) | Metrics, waits, file I/O, and **query text** (up to 4,000 characters per query hash) |
| Activity log | The same `history.db`, table `activity_log` | Windows file permissions | When monitoring started and stopped, app starts and exits, settings and connection changes with their old and new values, **your Windows user name and PC name**; 365-day retention |
| Diagnostic logs | `%AppData%\SqlVitals\logs\SqlVitals-yyyyMMdd.log` | Windows file permissions | Errors and warnings; **passwords and connection strings removed**; 14-day retention |
| Exported reports / CSVs / scripts | Wherever you save them | Your responsibility | Whatever you exported |
| Setup log | `%TEMP%\SqlVitals-Setup-*.log` | — | Install steps and paths only |

## Credentials

- `settings.dat` is encrypted with the Windows **Data Protection API** in *CurrentUser* scope. Only your Windows account on this PC can decrypt it. Copying the file to another PC or user doesn't reveal it.
- **It isn't lost to a bad read.** If another program (a sync or antivirus tool) has `settings.dat` open, SqlVitals tries again, and until it can read the file it won't save over it: a change you make says so, and you can try again. If the file can't be read at all (damaged, or copied from another Windows user or PC, so it can't be decrypted), a copy is kept as `settings.unreadable-<date>-<time>.dat` beside it before anything replaces it; settings that aren't encrypted are still read. Saves go to a temporary file first and then replace `settings.dat`, so a crash mid-save can't leave half a file. Added in 0.44.1 (#109).
- Credentials never appear in the connection list, the connection selector or error messages.
- The *Additional parameters* field rejects `Password` and `User ID`, so a secret can't be stored outside the encrypted fields.
- **Entra MFA** uses the interactive Microsoft sign-in. SqlVitals never sees your Microsoft password.
- **Windows Authentication** stores no secret.

## Connections to your servers

- Encryption defaults to **Mandatory**. **Strict** (TDS 8.0) is available for SQL Server 2022 and Azure SQL.
- **Trust server certificate** is off by default. Turn it on only for servers with self-signed certificates on networks you trust.
- Use a **dedicated low-privilege login** with just `VIEW SERVER STATE` (or `VIEW DATABASE STATE`). See [Server Impact and Permissions](Server-Impact-and-Permissions.md#permissions).
- SqlVitals' connections name themselves **SqlVitals** (`Application Name`, the `program_name` in `sys.dm_exec_sessions`), so they can be told apart in traces, audits and logon triggers, or limited with Resource Governor. An `Application Name` in *Additional parameters* takes precedence.
- **Generated scripts** keep names from the server (tables, users, logins) to one line inside `--` comments, so a name holding a line break can't turn part of itself into T-SQL that runs. The security audit never scripts away the access you are connected with. **CSV exports** write text that Excel would run as a formula with a leading `'`.

## Activity log

From 0.46.0 SqlVitals keeps an [activity log](Settings.md#activity-log): when monitoring was started and stopped and by what, when SqlVitals started and exited, every settings and connection change, and when history was cleared.

- **What it records about you:** each row has the **Windows account** (`DOMAIN\user`) SqlVitals ran as and the **PC name**, so a shared or copied history file still says who did it. It also records connection names, server names and setting values.
- **What it never records:** passwords, connection strings (including a connection's *Additional parameters*), access tokens, webhook URLs or any other secret. For those the row only says *changed*. Changes are worked out by comparing the settings file before and after each save against a fixed list of secret names, in one place (`SettingsDiff`), and tests check that no secret reaches the table.
- **Not a security audit trail.** It's a local file your Windows account can edit or delete. It is kept for 365 days, survives **Clear history**, and starts again if the history file is damaged and replaced.

## Query text and sensitive data

Query text captured from the server (in history, exports and copied grids) may contain **literal values**: customer names, IDs, amounts. Before sharing:

- **Export for AI reports**: read them before pasting into an external service.
- **`history.db`**: don't send it to others without checking. **Settings → Monitoring & history → Clear history** deletes the history, but keeps the activity log (with your Windows user and PC names); delete the file itself to remove that too, with SqlVitals closed.
- **Logs** are safe to attach to bug reports as far as credentials go, but may include server and database names.

## Installer

- Setup contains every file it installs and downloads nothing.
- Every file is verified against a SHA-256 manifest before being installed. Manifest paths are validated so nothing is written outside the install folder.
- Installs per-user without admin rights by default. Release builds can be code-signed.
- Uninstall leaves your data unless you tick the box to remove it.

## Reporting a vulnerability

Please open an issue at [github.com/aselasampath/sqlvitals/issues](https://github.com/aselasampath/sqlvitals/issues). For anything sensitive, ask for a private contact in the issue rather than posting details publicly.
