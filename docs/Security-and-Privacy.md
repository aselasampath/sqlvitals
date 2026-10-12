# Security and Privacy

What SqlVitals stores, where, how it's protected, and what leaves your PC.

## At a glance

- **No telemetry, no cloud.** SqlVitals makes no network connections except to the SQL Servers you configure.
- **Credentials are encrypted** with Windows DPAPI, per Windows user. That keeps them from other Windows users and off copies of the file; it doesn't keep them from other programs running as you. See [What DPAPI protects](#what-dpapi-protects-and-what-it-doesnt).
- **Passwords are optional on disk.** Untick *Remember password* and they stay in memory for the session only.
- **Secrets are scrubbed from everything that leaves the app**: passwords, access and bearer tokens, and webhook URLs are masked in the log files, the activity log, error messages and Export for AI reports. See [Secrets in messages, logs and reports](#secrets-in-messages-logs-and-reports).
- **History is local and unencrypted.** From 0.77.0 it keeps query text **without literals** by default, or none at all if you choose. See [Query text in the history](#query-text-in-the-history).
- **The activity log never holds a secret.** It records who changed what, but for a password or connection string only that it changed.

## What is stored, and where

| Data | Location | Protection | Contains |
|---|---|---|---|
| Saved connections and settings | `%AppData%\SqlVitals\settings.dat` | **DPAPI** (current user) | Server, database, auth type, login, password (only if *Remember password*), thresholds, preferences |
| Monitoring history | `%LocalAppData%\SqlVitals\history.db` | Windows file permissions (your profile) | Metrics, waits, file I/O, and the busiest queries' **text** (up to 4,000 characters per query hash), kept as *Store query text in history* says: **without literals** by default from 0.77.0 |
| Activity log | The same `history.db`, table `activity_log` | Windows file permissions | When monitoring started and stopped, app starts and exits, settings and connection changes with their old and new values, **your Windows user name and PC name**; 365-day retention |
| Diagnostic logs | `%AppData%\SqlVitals\logs\SqlVitals-yyyyMMdd.log` | Windows file permissions | Errors and warnings; **passwords, tokens, webhook URLs and connection strings removed**; 14-day retention |
| Exported reports / CSVs / scripts | Wherever you save them | Your responsibility | Whatever you exported |
| Setup log | `%TEMP%\SqlVitals-Setup-*.log` | — | Install steps and paths only |
| [Collector service](Collector-Service.md) settings (optional, from 0.73.0) | `%ProgramData%\SqlVitals\Collector\collector.json` | Folder locked to SYSTEM, Administrators, the service's account and the installing user; secrets **DPAPI** (*LocalMachine*) | Servers, logins, SQL passwords (protected), chat channels with their webhook and ping URLs (protected), sampling settings |
| Collector service history and logs | `%ProgramData%\SqlVitals\Collector\history.db`, `logs\Collector-yyyyMMdd.log` | The same locked folder | As the app's history and logs; the activity log has the service's account and PC name |

## Credentials

- `settings.dat` is encrypted with the Windows **Data Protection API** in *CurrentUser* scope. Only your Windows account on this PC can decrypt it. Copying the file to another PC or user doesn't reveal it.
- **It isn't lost to a bad read.** If another program (a sync or antivirus tool) has `settings.dat` open, SqlVitals tries again, and until it can read the file it won't save over it: a change you make says so, and you can try again. If the file can't be read at all (damaged, or copied from another Windows user or PC, so it can't be decrypted), a copy is kept as `settings.unreadable-<date>-<time>.dat` beside it before anything replaces it; settings that aren't encrypted are still read. Saves go to a temporary file first and then replace `settings.dat`, so a crash mid-save can't leave half a file. Added in 0.44.1 (#109).
- Credentials never appear in the connection list, the sidebar, the server menu or error messages.
- The *Additional parameters* field rejects `Password` and `User ID`, so a secret can't be stored outside the encrypted fields.
- **Entra MFA** uses the interactive Microsoft sign-in, through Windows' account manager (WAM) from 0.67.0. SqlVitals never sees your Microsoft password.
- **Windows Authentication** stores no secret.

### What DPAPI protects, and what it doesn't

`settings.dat` holds the connections (with any remembered password) and the chat channels (with their webhook URLs) encrypted by DPAPI in **CurrentUser** scope, with no extra secret of SqlVitals' own. The key is your Windows account's, unlocked by your Windows sign-in.

| Who | Can they read the passwords and webhook URLs in `settings.dat`? |
|---|---|
| Another Windows user on the same PC | **No.** Their DPAPI key isn't yours. |
| Anyone with a copy of the file: a backup, a synced folder, a support zip, another PC | **No.** It only decrypts as your account, signed in. |
| **Any program running as you**, while you're signed in | **Yes.** DPAPI decrypts for any process of your account, without asking. A script, a tool or malware running as you can read them, just as SqlVitals does. |
| An administrator of the PC | Not directly. But an administrator can run programs as you while you're signed in, and **domain administrators** can recover DPAPI keys with the domain's backup key. |

So DPAPI protects against other users and lost copies, not against software you run. That's the usual limit for a desktop tool, and why SqlVitals recommends **Windows authentication** (no password stored) or a low-privilege login with only `VIEW SERVER STATE`, and *Remember password* off on shared or unmanaged PCs. What isn't in `settings.dat` (the history, the activity log, the log files) isn't encrypted at all: only Windows file permissions on your profile protect it, and any program running as you, or an administrator, can read it.

The [collector service](#collector-service) uses DPAPI's **LocalMachine** scope instead, with the limits described there.

## Collector service

The optional [collector service](Collector-Service.md) (0.73.0, #172) runs as its own Windows account, so it can't read your `settings.dat`, and keeps its own `collector.json`. **The decision:** its secrets (SQL passwords, webhook and ping URLs) are encrypted with **DPAPI in LocalMachine scope**, with an entropy of SqlVitals' own, because the service, the admins and the app (which hands servers over to it) are different Windows users who all read the file. LocalMachine means any process on that PC could decrypt them, so the **folder's permissions** are what keep other users out: install, and the first command that writes the file, set full control for SYSTEM and Administrators, Modify for the service's account and the installing user, and nothing inherited from `%ProgramData%`. The encryption makes a copy of the file taken off the PC useless.

- **Windows authentication is recommended**: the service's account (or the PC's domain account) gets a login, and no secret is stored at all. SQL logins are for servers outside the domain. Entra MFA can't be used, since nobody is there to sign in.
- **Secrets are never command-line arguments.** The command line takes them at a hidden prompt or from standard input, and never prints them; a secret typed into `collector.json` in clear text is refused rather than used.
- The service's logs are scrubbed as the app's are.
- **The share copy** (from 0.74.0, off by default) is the whole history, query text included, written to the folder you choose for other PCs to read. It holds no secret: the settings stay in the locked folder. But anyone who can read the share can read the history, so share it only with the people who could see these servers in SqlVitals.
- **Handing a server over** gives the service the connection's SQL login and password (only when it's saved), encrypted for the PC as above, and the webhook URLs of its chat channels.

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

## Query text in the history

Every snapshot saves the numbers of the busiest queries (CPU, reads, executions) by their **query hash**, and the text of each query once. A query's text can hold **literal values**: customer names, email addresses, amounts, and sometimes a password in dynamic SQL (`CREATE LOGIN … WITH PASSWORD = '…'`). From 0.77.0 (#175), **Settings → Monitoring & history → Store query text in history** decides what is kept:

| Choice | What `history.db` keeps |
|---|---|
| **Without literals** (default for a new installation) | The statement with every string, number, money and binary value replaced by `?`, found by the T-SQL lexer (ScriptDom): `WHERE Email = 'ann@example.com' AND Id IN (1, 2)` is kept as `WHERE Email = ? AND Id IN (?, ?)`. Names of tables, columns and variables, and comments, are kept; text in "double quotes" goes too, since with `QUOTED_IDENTIFIER OFF` it is a string. The statement inside `EXEC('…')` and `sp_executesql N'…'` is kept the same way, its own values replaced; any other string is a value. Text cut off at 4,000 characters inside a string loses all of that string. |
| **Full** | The text exactly as SQL Server has it, as every version before 0.77.0 kept it. |
| **Off** | No text at all, only the hash. The numbers are kept; pages that show history show those queries without text. |

- **An installation from before 0.77.0 keeps Full** until you choose: SqlVitals asks once, the first time its window opens after the upgrade. Closing the question keeps Full.
- **Changing the setting doesn't touch what's already saved.** Text read from then on is kept the new way. To remove what's there, press **Clear stored query text…** in the same place: it deletes the text of every query, for every connection, and overwrites the freed space in the file so the text can't be read back out of it. The numbers stay, text is read again as the setting says, and the activity log records the clear. When the [collector service](Collector-Service.md) is installed on this PC, it is asked to clear its own history's text too, within a few seconds (or with `SqlVitals.Collector clear-query-text`).
- **The collector service** keeps its own setting: the app passes its choice on when it hands a server over and whenever you change it, and `SqlVitals.Collector set --query-text full|without-literals|off` sets it by hand. A service set up before 0.77.0 keeps Full until then.
- **Not covered:** the pages read query text live from the server and show it in full, and **Export for AI** writes it as the server has it (with secrets masked, below). Read an export before pasting it into an external service. **Clear history** deletes the history but keeps the activity log (with your Windows user and PC names); delete the file itself, with SqlVitals closed, to remove that too.

## Secrets in messages, logs and reports

A driver, a parser or a web service can echo a secret back in an error message. From 0.77.0 (#175) one shared redaction is applied wherever text leaves SqlVitals: the **log files**, the **activity log** (old value, new value and detail), **error messages** (every message box, the top bar's errors and the Settings status lines) and **Export for AI** reports. It masks:

- `Password=`, `Pwd=` and `Access Token=` values in connection strings, and `PASSWORD = '…'` in SQL: `Password=*****`.
- **Bearer tokens** (`Authorization: Bearer …`), OAuth token fields (`access_token`, `refresh_token`, `id_token`) and JSON web tokens anywhere in the text.
- **Webhook URLs** of Slack, Teams, Power Automate and Logic Apps: `[webhook URL removed]`.

The log files also replace whole connection strings with `[connection string removed]`. The collector service uses the same code. **Logs** are then safe to attach to bug reports as far as credentials go, but may include server and database names.

## Installer

- Setup contains every file it installs and downloads nothing.
- Every file is verified against a SHA-256 manifest before being installed. Manifest paths are validated so nothing is written outside the install folder.
- Installs per-user without admin rights by default. Release builds can be code-signed.
- Uninstall leaves your data unless you tick the box to remove it.

## Reporting a vulnerability

Please open an issue at [github.com/aselasampath/sqlvitals/issues](https://github.com/aselasampath/sqlvitals/issues). For anything sensitive, ask for a private contact in the issue rather than posting details publicly.
