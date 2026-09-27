# Installation

SqlVitals installs with one guided setup file, or runs from a [portable zip](#portable-zip-no-installer) with no install at all. You don't need .NET, admin rights or anything installed on the SQL Server.

## What you need

| | Requirement |
|---|---|
| **Your PC** | Windows 10 (version 1607 or later) or Windows 11, 64-bit |
| **The database** | SQL Server (on-premises or in a VM), Azure SQL Managed Instance, or Azure SQL Database |
| **Permission** | `VIEW SERVER STATE` (SQL Server, Managed Instance) or `VIEW DATABASE STATE` (Azure SQL Database). See [below](#ask-your-dba-for-permission). |
| **Network** | Your PC must be able to reach the server's SQL port (1433 by default) |

## Install

1. Open the [latest release](https://github.com/aselasampath/sqlvitals/releases/latest) and, under **Assets**, download `SqlVitals-Setup-<version>.exe`. The release notes list its SHA-256, and a `.sha256` file next to it lets you [check that the download is intact](#check-the-download).
2. Run it. The wizard goes through **Welcome → System check → Options → Review → Install → Finish**.
3. **System check** confirms your Windows version, free disk space and write permission, and that SqlVitals isn't already running. If something fails, it tells you how to fix it and offers **Check again**.
4. **Options**: by default SqlVitals installs for you only, in `%LocalAppData%\Programs\SqlVitals`, with Start menu and desktop shortcuts. No admin rights are needed. You can pick another folder. A folder outside your user profile, with admin rights, installs for all users.
5. Click **Install**, then **Launch SqlVitals** on the last page.

On first launch the app opens **Settings** so you can add your first connection. Continue with [Getting Started](Getting-Started.md).

> **If a step fails:** Setup offers **Retry** or **Cancel**. Cancel rolls everything back and restores your previous installation exactly.

> **Windows SmartScreen:** Setup isn't code-signed yet, so Windows may warn you before running it. Choose **More info → Run anyway** if you downloaded it from this repository.

### Check the download

In PowerShell, in your Downloads folder:

```powershell
(Get-FileHash .\SqlVitals-Setup-<version>.exe -Algorithm SHA256).Hash
```

The result should match the SHA-256 in the release notes and in the `.sha256` file (ignore upper/lower case). If it doesn't, download it again. The same check works for the portable zip.

## Portable zip (no installer)

If you can't or would rather not run Setup, every release since 0.43.0 also has a portable zip. It is the same self-contained app that Setup installs.

1. Open the [latest release](https://github.com/aselasampath/sqlvitals/releases/latest) and, under **Assets**, download `SqlVitals-<version>-win-x64-portable.zip` (and its `.sha256` if you want to [check it](#check-the-download)).
2. Right-click the zip → **Properties** → tick **Unblock** → **OK**. This stops Windows warning about each file downloaded from the internet.
3. Unzip it anywhere you can write to, such as `Documents` or a USB drive. It creates a `SqlVitals-<version>` folder.
4. Run `SqlVitals.Desktop.exe` in that folder.

How it differs from Setup:

| | Setup | Portable zip |
|---|---|---|
| Start menu and desktop shortcuts | ✅ | ❌ Pin `SqlVitals.Desktop.exe` yourself if you want one |
| Listed in *Settings → Apps*, with uninstall | ✅ | ❌ Delete the folder to remove it |
| Upgrade | Run the new Setup | Unzip the new version, then delete the old folder |
| Admin rights, .NET install | Not needed | Not needed |

**Where your data lives:** connections, settings and logs are kept in `%AppData%\SqlVitals`, and monitoring history in `%LocalAppData%\SqlVitals`, not in the unzipped folder. So a new portable version, or an installed SqlVitals on the same PC, picks up the same connections and history. Deleting the folder doesn't remove them; delete those two folders too if you want everything gone. If you edited `appsettings.json` in the unzipped folder, copy it into the new version's folder when you upgrade.

## Where installers come from

Every installer is built by GitHub Actions from the repository's code, never on someone's PC:

| Source | When it's built | Where to download it | Kept for |
|---|---|---|---|
| **Release** (recommended) | When a version is tagged, e.g. `v0.34.2` | [Releases](https://github.com/aselasampath/sqlvitals/releases) → the version → **Assets**. No GitHub account needed. | Always |
| **Release run artifact** | Same build as the release | *Actions* → *Release – Build and publish SqlVitals Setup* → the run → **Artifacts** → `SqlVitals-Setup-<version>` or `SqlVitals-<version>-win-x64-portable`. Needs a GitHub sign-in. | 90 days |
| **Pull request** | Every push to a PR to `main` | See below | 14 days |

If a release shows only *Source code* under **Assets** (as 0.34.0 does), its Setup is in that release's run artifacts. 0.34.2 has the same features, with Setup attached.

## Get the latest version

A release is published each time a version is tagged, and it can lag behind the code on `main`. To get the newest features before a release:

- **From a pull request:** every PR to `main` builds an installer. Open the PR's *Checks* → the *Build Setup* run → **Artifacts** → `SqlVitals-Setup-<version>-pr<N>` or `SqlVitals-<version>-win-x64-portable-pr<N>` (kept for 14 days).
- **Build it yourself:** see [Building and Contributing](Building-and-Contributing.md#build-the-installer).

## Upgrade, repair or uninstall

- **Upgrade:** download the newer Setup from the [latest release](https://github.com/aselasampath/sqlvitals/releases/latest) and run it. It detects the existing install and offers **Upgrade**.
- **Repair:** *Windows Settings → Apps → SqlVitals → Modify*, or run Setup again and choose **Repair**.
- **Uninstall:** *Windows Settings → Apps → SqlVitals → Uninstall*.

Your saved connections and monitoring history are **kept** on upgrade, repair and uninstall. Uninstall deletes them only if you tick the box to do so. If you edited `appsettings.json` in the install folder, an upgrade keeps your copy and writes the new default beside it as `appsettings.json.new`.

## Ask your DBA for permission

SqlVitals needs permission to read SQL Server's diagnostic views. Send your DBA the statement that fits your server:

```sql
-- SQL Server (on-premises or VM) and Azure SQL Managed Instance
GRANT VIEW SERVER STATE TO [your_login];

-- Azure SQL Database (run in the user database)
GRANT VIEW DATABASE STATE TO [your_user];
```

That covers every screen. The **SP Trace** screen works with it too, in a summary mode. To see every individual stored-procedure call, it needs one more permission. See [SP Trace](SP-Trace.md#permissions).

Not sure what you have? Run this in SSMS or Azure Data Studio. `1` means yes:

```sql
-- SQL Server / Managed Instance
SELECT HAS_PERMS_BY_NAME(NULL, NULL, 'VIEW SERVER STATE') AS CanReadDmvs;

-- Azure SQL Database
SELECT HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DATABASE STATE') AS CanReadDmvs;
```

---

### Under the hood

- Setup (`SqlVitals.Setup.exe`) targets .NET Framework 4.7.2, which ships with Windows 10 1803+ and Windows 11, so it runs on a clean PC. The app it installs is **self-contained**: the .NET 8 runtime is bundled.
- Nothing is downloaded during install. The package is a zip embedded in the Setup exe, with a manifest of every file's size and SHA-256 hash.
- **Transactional install:** files are unpacked to `.setup-staging`, verified against the manifest, then swapped in. Each replaced file is moved to `.setup-backup` first, so a cancel restores the previous version.
- Only files listed in `install-manifest.txt` are replaced or removed on upgrade and uninstall. Manifest paths are validated so no file can land outside the install folder.
- Setup hands over to a temporary copy of itself before changing the install folder, and always launches SqlVitals non-elevated.
- The Setup log is `%TEMP%\SqlVitals-Setup-*.log`. It records steps and paths only.
- Building the installer yourself is covered in [Building and Contributing](Building-and-Contributing.md#build-the-installer).
