# Collector Service

*For DBAs who want SqlVitals to keep watching when nobody is signed in.* The **SqlVitals Collector** is an **optional** Windows service. It runs on an always-on PC or server and watches SQL Server around the clock. It collects samples, raises alerts, keeps health scores and posts to Slack or Teams, the same way the app does. It is new in 0.73.0 (#172).

You don't need it. Without it, SqlVitals works as before: the app monitors while it runs, in the window or in the [notification area](Notifications-and-Tray.md). Use the service when a 3 a.m. problem has to be caught even with your laptop closed.

> **In 0.73.0** the service is set up with its command line, `SqlVitals.Collector.exe`. In the next release (the rest of #172) the app gets these: start and stop the service, a question when monitoring starts (*watch this server with the service?*), handing a server over to the service and taking it back, and Home, Overview, history pages and Alerts reading the service's history.

## In short

- **One service per PC**, with its own settings (`collector.json`) and its own history (`history.db`). It uses the same file format as the app, so the app can read it.
- It **runs as its own account**, `NT SERVICE\SqlVitalsCollector` by default. It reads each server with **Windows authentication** (recommended) or a **SQL login**. Entra MFA isn't possible here, because nobody is there to sign in.
- It **picks up changes on its own.** The settings file is checked every 10 seconds, so a server added with the command line starts without a restart.
- **Chat channels work as in the app.** Alerts, back to normal, *stopped watching* (with *because the SqlVitals Collector service stopped* when the service stops) and [heartbeats](Chat-Notifications.md#heartbeat) are posted, with *SqlVitals on SRV01 (collector service)* as the sender.
- **Not a team platform.** There is no web UI, no central database, no roles. See [What SqlVitals is not](../README.md).

## Install

You need an always-on Windows 10/11 or Windows Server PC that can reach your SQL Servers (and, for chat, `https://` to Slack or Teams). You need to be an administrator on it.

1. Download **SqlVitals-Collector-&lt;version&gt;-win-x64.zip** from the [latest release](https://github.com/aselasampath/sqlvitals/releases/latest). The .NET runtime is included.
2. Unzip it to a folder every account can read, such as `C:\Program Files\SqlVitals Collector`. Install refuses a folder inside a user's profile, because the service's account can't read it.
3. Open a command prompt **as administrator** in that folder and run:

   ```text
   SqlVitals.Collector.exe install --start
   ```

   Install does the following:
   - registers the service **SqlVitals Collector** (`SqlVitalsCollector`). It starts automatically (delayed) and restarts after a crash: twice after a minute, then after five minutes.
   - lets **you** (the Windows user who ran install) start and stop it without an admin prompt. The app uses this in the next release.
   - creates `%ProgramData%\SqlVitals\Collector` and locks it to SYSTEM, Administrators, the service's account and you (see [Security](#security)).
   - adds the *SqlVitals Collector* source to the Windows Application event log.
   - prints the T-SQL that gives the service's account a login on each server (below).

### Which account it runs as

| `--account` | What it is | Reaches other servers as |
|---|---|---|
| *(none)* | `NT SERVICE\SqlVitalsCollector`, a virtual account: no password, no rights on the PC beyond its folder | the PC's domain account, `DOMAIN\PCNAME$` |
| `CONTOSO\svc-vitals$` | A group managed service account (gMSA): Windows keeps its password | itself |
| `CONTOSO\svc-vitals --password-stdin` | An ordinary domain account; pipe its password in | itself |
| `NT AUTHORITY\NetworkService` | Built in | the PC's domain account |

## Give it a login on each server

The service needs the same permission as the app's monitoring: **`VIEW SERVER STATE`** (`VIEW DATABASE STATE` on Azure SQL Database). See [Server Impact and Permissions](Server-Impact-and-Permissions.md#permissions). Install prints the script for the account you chose. With the default account, run this on each server:

```sql
CREATE LOGIN [CONTOSO\DBAPC01$] FROM WINDOWS;   -- the collector PC's domain account
GRANT VIEW SERVER STATE TO [CONTOSO\DBAPC01$];
```

For a SQL Server on the collector PC itself, the login is `[NT SERVICE\SqlVitalsCollector]`. The service's sessions show `program_name` **SqlVitals Collector**, so they can be told apart from the app's (*SqlVitals*).

## Add servers and chat channels

Every command takes `--data-dir <folder>` to use a folder other than `%ProgramData%\SqlVitals\Collector`. `SqlVitals.Collector.exe help` lists every command and option.

```text
SqlVitals.Collector.exe add-server --server sql01 --name Sales-Prod
SqlVitals.Collector.exe add-server --server sql02.contoso.com --database HR --sql-login vitals
SqlVitals.Collector.exe add-channel --name "DBA team" --heartbeat 6h --ping
SqlVitals.Collector.exe test
SqlVitals.Collector.exe list
```

| Command | What it does |
|---|---|
| `add-server --server <name>` | Adds a server. Options: `--database`, `--name` (as shown in chat messages), `--sql-login <user>` (otherwise Windows authentication), `--encrypt mandatory\|optional\|strict`, `--trust-server-certificate`, `--connect-timeout <s>`, `--id <guid>` |
| `set-password <server>` | Sets a SQL login's password again, for example after the file was copied from another PC |
| `remove-server <server>` | Stops watching it. Its history is kept for the retention days |
| `add-channel --name <name>` | Adds a Slack or Teams channel. Options: `--service slack\|teams` (otherwise worked out from the URL), `--servers a,b` (otherwise all), `--critical-only`, `--no-ends`, `--heartbeat off\|<1–12>h\|daily@HH:mm`, `--ping` (a dead man's switch URL as well) |
| `remove-channel <name>` | Removes a channel |
| `set` | `--sample-seconds 5–300` (default 15), `--detail-minutes 1\|5\|15\|30`, `--retention-days 7\|14\|30\|90`, `--alert-samples 1\|2\|3\|5\|10`, `--command-timeout <s>` |
| `test` | Reads each server once and posts a test message to each channel. It reads **as you**, not as the service's account |
| `list` | The servers, channels and settings, the service's state, and anything that needs fixing |
| `run` | Watches in this console until Ctrl+C: for a first try, before installing |
| `install`, `uninstall` | See [Install](#install) and [Update and uninstall](#update-and-uninstall) |

**Secrets are never command-line arguments**, where they would stay in the shell's history. A password, webhook URL or ping URL is typed at a hidden prompt, or piped in: `--password-stdin` for `add-server` and `set-password`, and `--stdin` for `add-channel` (the webhook URL on the first line, the ping URL on the second).

`collector.json` is plain JSON, so you can read it. You can change the sampling settings by hand. Its secrets are stored only in their protected form (`"dpapi:…"`). A secret typed into the file in clear text is refused: the server isn't watched, or the channel isn't used, and `list` says so.

## Security

**The decision (#172): DPAPI with the LocalMachine scope, a locked folder, and Windows authentication recommended.**

- The app's `settings.dat` is encrypted for *your* Windows user (DPAPI CurrentUser), so the service's account can't read it. The service therefore has its own file.
- The service, the admins and the app (which will hand servers over in the next release) are different Windows users who must all read that file. So its secrets (SQL passwords, webhook and ping URLs) are encrypted with **DPAPI LocalMachine**, with an entropy of SqlVitals' own. Any process on the PC could decrypt them, so **the folder's permissions are what keep other users out**. Install, and the first command that writes the file, set those permissions: full control for SYSTEM and Administrators, Modify for the service's account and the installing user, and nothing inherited from `%ProgramData%`, which every user can read. The encryption makes a copy of the file useless off the PC: a backup, a file share, a support zip.
- **Windows authentication** keeps no secret at all, and is the recommended choice: give the service's account (or the PC's domain account) a login. A SQL login is there for servers outside the domain.
- Logs never contain a password, connection string or webhook URL (the same scrubbing as the app's).

See [Security and Privacy](Security-and-Privacy.md#collector-service).

## Files

| What | Where |
|---|---|
| Settings | `%ProgramData%\SqlVitals\Collector\collector.json` |
| History | `%ProgramData%\SqlVitals\Collector\history.db`: the app's [schema](Monitoring-History-Database.md), with the service's starts, stops and the servers it watched in the activity log, under source *collector service* |
| Logs | `%ProgramData%\SqlVitals\Collector\logs\Collector-yyyyMMdd.log`, kept 14 days. Warnings and errors also go to **Event Viewer → Windows Logs → Application**, source *SqlVitals Collector* |

## Update and uninstall

- **Update:** `sc stop SqlVitalsCollector`, replace the folder's files with the new zip's, then `sc start SqlVitalsCollector`. Settings and history stay in `%ProgramData%`.
- **Uninstall**, as administrator: `SqlVitals.Collector.exe uninstall`. This stops the service, removes it and its event log source, and keeps its settings and history. Add `--delete-data` to delete them too.

## Troubleshooting

| You see | Why, and what to do |
|---|---|
| A server is never watched; `list` says *no password* | A SQL login without a password, or one encrypted on another PC: run `set-password` |
| *can't be read: Login failed for user 'CONTOSO\DBAPC01$'* in the log | The service's account has no login on that server: run the script from [above](#give-it-a-login-on-each-server) |
| The service doesn't start; the event log says the settings couldn't be read | `collector.json` isn't valid JSON (after a hand edit): the message gives the line. Fix it, or run `list` to see the same message |
| Nothing in a chat channel | Run `test`. A channel whose URL can't be decrypted on this PC is listed under *Needs fixing* by `list`: remove it and add it again |
