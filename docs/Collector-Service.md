# Collector Service

*For DBAs who want SqlVitals to keep watching when nobody is signed in.* The **SqlVitals Collector** is an **optional** Windows service. It runs on an always-on PC or server and watches SQL Server around the clock. It collects samples, raises alerts, keeps health scores and posts to Slack or Teams, the same way the app does. It is new in 0.73.0 (#172).

You don't need it. Without it, SqlVitals works as before: the app monitors while it runs, in the window or in the [notification area](Notifications-and-Tray.md). Use the service when a 3 a.m. problem has to be caught even with your laptop closed.

> **From 0.74.0** the app works with it. When the service is installed on the same PC, **▶ Start** asks where to watch a server. **⚙️ Settings → Collector service** starts and stops the service, and hands each server over or takes it back. Home, Overview, the history pages and Alerts show a handed-over server from the service's history. The app can also read a collector on another PC, from its [share copy](#a-collector-on-another-pc). In 0.73.0 the service was set up with its command line only.

## In short

- **One service per PC**, with its own settings (`collector.json`) and its own history (`history.db`). It uses the same file format as the app, so the app can read it.
- It **runs as its own account**, `NT SERVICE\SqlVitalsCollector` by default. It reads each server with **Windows authentication** (recommended) or a **SQL login**. Entra MFA isn't possible here, because nobody is there to sign in.
- It **picks up changes on its own.** The settings file is checked every 10 seconds, so a server added with the command line starts without a restart.
- **Chat channels work as in the app.** Alerts, back to normal, *stopped watching* (with *because the SqlVitals Collector service stopped* when the service stops) and [heartbeats](Chat-Notifications.md#heartbeat) are posted, with *SqlVitals on SRV01 (collector service)* as the sender.
- **One watcher per server.** A server handed to the service isn't collected by the app as well, and its history reads as one, the app's part and the service's.
- **Not a team platform.** There is no web UI, no central database, no roles. See [What SqlVitals is not](../README.md).

## From the app

With the service installed on this PC, by your Windows user:

- **▶ Start asks.** Pressing Start on a server (on its Overview, its tile on Home, the sidebar or Live charts) asks *Where should Sales-Prod be watched?*
  - **Watch with the collector service** hands the server over, and starts the service if it's stopped.
  - **Watch in SqlVitals** works as before.

  Tick **Don't ask again** to keep your answer. Change it later in **Settings → Collector service → When monitoring starts**. Start doesn't ask when the service is not installed, for an Entra MFA connection (the service can't sign in), or for a SQL login whose password isn't saved.
- **Handing over** writes the server to the service's `collector.json`. The server keeps its id, so the app and the service record its history as one server's. It takes the server's SQL login with its password, its own health thresholds, and **the chat channels that post for it**, with their choices and heartbeat, so its alerts keep reaching the same channels. The app stops collecting the server within 10 seconds, and its chat channels hear *SqlVitals stopped watching Sales-Prod because the SqlVitals Collector service watches it now*.
- **Settings → Collector service** has the controls:
  - the service's state, with **Start** and **Stop**. Install lets you do both without an admin prompt. If another user installed it, Windows asks for an admin.
  - what Start does;
  - your servers, each with **Hand over** or **Take back**;
  - a [collector on another PC](#a-collector-on-another-pc).
- **Home and Overview** show a handed-over server from the service's history:
  - its last score, and the alerts the service has open, which also give its status;
  - its charts, live and past;
  - *Sample 13:46:01 · by the collector service* on its tile.

  The history keeps each sample's score but not the checks behind it, so the score's breakdown isn't shown for it.
- **If the service stops**, its servers show **Off**, with *Handed to the SqlVitals Collector service, which is stopped*. ▶ Start then offers to start it. Taking a server back gives it to the app again, as its settings say.
- **Alerts and the history pages** read both files: the app's own and the service's.

## A collector on another PC

A collector on an always-on server can share its history with the app on your laptop. A live history file can't be read safely over a network share (SQLite's WAL mode needs both on one PC), so the collector writes a **share copy** every few minutes and the app reads that copy, read-only.

1. On the collector's PC: `SqlVitals.Collector.exe set --share-copy-minutes 5 --share-copy-folder D:\SqlVitalsShare`. The service's account needs Modify on that folder. Without `--share-copy-folder`, the copy goes to the data folder's `share\`.
2. Share the folder read-only, to the people who should see it. The copy is the whole history, query text included (see [Security and Privacy](Security-and-Privacy.md#collector-service)).
3. In the app: **Settings → Collector service → A collector on another PC**. Enter `\\DBA01\SqlVitalsShare`, then **Save**.

Your connections that match one of its servers (same server and database, ignoring case) are shown from its copy. This PC doesn't collect them while it does. The copy is up to N minutes old, so their tiles are as fresh as the copy.

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
   - lets **you** (the Windows user who ran install) start and stop it without an admin prompt, as the app does from 0.74.0, and gives you Modify on its data folder, so the app can hand servers over.
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
| `set` | `--sample-seconds 5–300` (default 15), `--detail-minutes 1\|5\|15\|30`, `--retention-days 7\|14\|30\|90`, `--alert-samples 1\|2\|3\|5\|10`, `--command-timeout <s>`, `--share-copy-minutes 5–1440\|0` and `--share-copy-folder <folder>` ([share copy](#a-collector-on-another-pc)), from 0.76.0 `--backup-rpo-full <age>` and `--backup-rpo-log <age>` (`7d`, `1h`, as on the Backups page): what the [Backups past RPO](Health-Dot-and-Background-Monitoring.md#what-is-checked) check counts against. The app sets them to its own when it hands a server over |
| `test` | Reads each server once and posts a test message to each channel. It reads **as you**, not as the service's account |
| `list` | The servers, channels and settings, the service's state, and anything that needs fixing |
| `run` | Watches in this console until Ctrl+C: for a first try, before installing |
| `install`, `uninstall` | See [Install](#install) and [Update and uninstall](#update-and-uninstall) |

**Secrets are never command-line arguments**, where they would stay in the shell's history. A password, webhook URL or ping URL is typed at a hidden prompt, or piped in: `--password-stdin` for `add-server` and `set-password`, and `--stdin` for `add-channel` (the webhook URL on the first line, the ping URL on the second).

`collector.json` is plain JSON, so you can read it. You can change the sampling settings by hand. Its secrets are stored only in their protected form (`"dpapi:…"`). A secret typed into the file in clear text is refused: the server isn't watched, or the channel isn't used, and `list` says so.

## Security

**The decision (#172): DPAPI with the LocalMachine scope, a locked folder, and Windows authentication recommended.**

- The app's `settings.dat` is encrypted for *your* Windows user (DPAPI CurrentUser), so the service's account can't read it. The service therefore has its own file.
- The service, the admins and the app (which hands servers over) are different Windows users who must all read that file. So its secrets (SQL passwords, webhook and ping URLs) are encrypted with **DPAPI LocalMachine**, with an entropy of SqlVitals' own. Any process on the PC could decrypt them, so **the folder's permissions are what keep other users out**. Install, and the first command that writes the file, set those permissions: full control for SYSTEM and Administrators, Modify for the service's account and the installing user, and nothing inherited from `%ProgramData%`, which every user can read. The encryption makes a copy of the file useless off the PC: a backup, a file share, a support zip.
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
