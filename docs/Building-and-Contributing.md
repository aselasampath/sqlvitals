# Building and Contributing

*For developers.* Build SqlVitals from source, run the tests, build the installer, release a new version, and add a new screen.

## Prerequisites

- **.NET SDK 9.0.200 or later** (.NET 10 works too). The projects target .NET 8, but the `.slnx` solution format needs a 9.0.200+ SDK. With only the .NET 8 SDK, build projects one by one, as CI does: `dotnet build SqlVitals\Desktop`.
- **Windows 10/11**. WPF is Windows-only, and the Installer tests run on .NET Framework 4.7.2.
- A SQL Server or Azure SQL to point at, with `VIEW SERVER STATE` / `VIEW DATABASE STATE`.
- Optional: **Visual Studio 2022 17.14+** (for `.slnx`).

## Build, test, run

```powershell
git clone https://github.com/aselasampath/sqlvitals.git
cd sqlvitals

dotnet build SqlVitalsDashboard.slnx     # all six projects
dotnet test                              # Engine.Tests + Desktop.Tests + Installer.Tests
dotnet run --project SqlVitals\Desktop   # start the app
```

A few `NU1701` warnings from LiveCharts' SkiaSharp/OpenTK dependencies are expected and harmless.

One test project at a time:

```powershell
dotnet test SqlVitals\Engine.Tests
dotnet test SqlVitals\Desktop.Tests      # the app's services; Windows only (WPF, DPAPI)
dotnet test SqlVitals\Installer.Tests
```

**Rebuild while the app is running** (otherwise the build fails with *file locked*). Closing the window doesn't exit SqlVitals by default: it keeps running in the notification area. Choose **Exit** from its icon's menu, or:

```powershell
Get-Process -Name "SqlVitals.Desktop" -ErrorAction SilentlyContinue | Stop-Process -Force
dotnet build SqlVitals\Desktop --nologo
Start-Process "SqlVitals\Desktop\bin\Debug\net8.0-windows\SqlVitals.Desktop.exe"
```

**Visual Studio:** open `SqlVitalsDashboard.slnx`, set `SqlVitals.Desktop` as the startup project, press **F5**.

**Only one SqlVitals runs per Windows session**, whichever exe it is. If an installed copy is running (perhaps hidden in the notification area), starting your dev build just brings the installed copy to the front and exits. Exit the installed copy first. They share `settings.dat` and `history.db`, which is why they can't run side by side.

## Repository layout

```text
SqlVitalsDashboard.slnx
.github/workflows/pr-setup.yml   CI: tests + Setup build on every PR to main
.github/workflows/release.yml    Release: tag vX.Y.Z → tests, Setup build, GitHub Release
TODO/wait-stats-ui/              Parked React UI (not in the solution)
SqlVitals/
  Desktop/          WPF app: Pages/, Controls/, Windows/, Helpers/, Services/, Styles/
  Engine/           Models/, Repositories/, History/, Regressions/, Scripting/, Export/,
                    Filtering/, Monitoring/, Errors/, Controllers/ (+ appsettings.json)
  Engine.Tests/     xUnit
  Desktop.Tests/    xUnit for the app's services (settings file, monitoring)
  Installer/        Setup wizard: Core/ (logic, no WPF), Views/, Build-Installer.ps1
  Installer.Tests/  xUnit for Installer/Core
  Branding/         Icon sources + Build-Icon.ps1
```

See [Architecture](Architecture.md) for how the pieces fit together.

## Build the installer

```powershell
.\SqlVitals\Installer\Build-Installer.ps1                                # unsigned dev build
.\SqlVitals\Installer\Build-Installer.ps1 -CertificateThumbprint <sha1>  # signed release build
# artifacts\SqlVitals-Setup-<version>.exe (+ .sha256)
# artifacts\SqlVitals-<version>-win-x64-portable.zip (+ .sha256)
```

The script publishes `SqlVitals.Desktop` **self-contained** for win-x64 and zips that folder, inside a `SqlVitals-<version>` folder, as the portable zip. It then writes a manifest with each file's size and SHA-256, zips it, and embeds the zip in `SqlVitals.Setup.exe`. A plain `dotnet build` also builds Setup, but **without the payload**, and that Setup says so when it starts.

**Version:** `<Version>` in `SqlVitals/Desktop/SqlVitals.Desktop.csproj` is the release number. CI and the installer both read it.

## CI

`.github/workflows/pr-setup.yml` runs on every pull request to `main` (opened, pushed to, reopened, ready for review) on `windows-latest`:

1. Reads the version from the Desktop `.csproj`.
2. Runs `Installer.Tests`, `Engine.Tests`, then `Desktop.Tests` (Release).
3. Builds an **unsigned** Setup and portable zip with `Build-Installer.ps1`.
4. Posts the file names, sizes, SHA-256 hashes and commit to the run summary.
5. Uploads `SqlVitals-Setup-<version>-pr<N>` and `SqlVitals-<version>-win-x64-portable-pr<N>` as artifacts, kept for **14 days** (Actions tab → run → *Artifacts*).

A new push cancels the build still running for the previous one. PR builds are unsigned, so Windows SmartScreen may ask to confirm (*More info → Run anyway*).

## Release a new version

Releases are built by `.github/workflows/release.yml` when a version tag is pushed. Nobody builds or uploads Setup by hand.

1. **Bump the version** in a PR: `<Version>` in `SqlVitals/Desktop/SqlVitals.Desktop.csproj` (and the version in this wiki's Home banner and footer). Merge it.
2. **Tag the merge commit on `main` and push the tag:**

   ```powershell
   git switch main; git pull
   git tag v0.34.2              # "v" + <Version>, exactly
   git push origin v0.34.2
   ```

   > **Don't publish the release on GitHub's *Draft a new release* page.** A published release is immutable, so the workflow can't attach Setup to it. If you prefer the web page, create the tag there but click **Save draft**. The workflow then attaches Setup and publishes the draft for you.

3. **Watch the run:** *Actions → Release – Build and publish SqlVitals Setup*, or `gh run watch`. It takes about as long as a PR build.

What the workflow does:

1. Checks that the tag is `v` + `<Version>`. If it isn't, it stops before building anything, so the tag, the Setup file name and the version in *Settings → Apps* always agree.
2. Runs `Installer.Tests` and `Engine.Tests` (Release).
3. Builds Setup and the portable zip with `Build-Installer.ps1`.
4. Uploads `SqlVitals-Setup-<version>` (the `.exe` and `.sha256`) and `SqlVitals-<version>-win-x64-portable` (the `.zip` and `.sha256`) as run artifacts, kept for **90 days**.
5. Creates the GitHub Release **as a draft**, titled *SqlVitals &lt;version&gt;*, with install and portable sections (each with its SHA-256) followed by notes generated from the PRs merged since the previous tag. It attaches all four files and checks that each arrived at full size.
6. Publishes the release. A tag with a suffix, such as `v1.0.0-beta.1`, becomes a **pre-release**. Otherwise the release becomes *Latest*.

**Why a draft first:** releases in this repository are **immutable**. Once a release is published, its files can't be changed and its tag can't be reused. If anything fails before publishing, the release stays a draft you can fix or delete, and the tag can be pushed again.

| Problem | What to do |
|---|---|
| *Tag vX doesn't match &lt;Version&gt;* | Delete the tag (`git push origin :refs/tags/vX; git tag -d vX`), then either bump `<Version>` or tag the right number. |
| Tests or the build failed | Fix on `main` in a PR, delete the tag as above, then tag the new merge commit. |
| Failed after the draft was created (e.g. a network error) | Re-run the workflow (*Re-run all jobs*). The draft is reused: its files are uploaded again, checked, and it is published. If the problem is in the code, delete the draft on the Releases page and the tag, fix it, and tag again. |
| The tag already has a published release (e.g. published from the GitHub web page) | The release can't be changed, so it has no Setup. The workflow says so in the run summary, and Setup is still in the run's **Artifacts** for 90 days. The tag of an immutable release can't be reused, so to put Setup on a release, bump `<Version>` (e.g. `0.34.0` → `0.34.1`) and push the new tag with `git`. |

Releases are **unsigned** for now. Code signing and an in-app update check are tracked in [#49](https://github.com/aselasampath/sqlvitals/issues/49).

## Contribution workflow

- Work is tracked in [issues](https://github.com/aselasampath/sqlvitals/issues). Branches are named `<issue>-<short-slug>`, e.g. `33-configure-health-thresholds-in-settings`.
- Open a PR to `main`. CI must pass, and reviewers can download the PR's Setup from the run's artifacts.
- Squash-merge titles follow `<Title> (#<issue>) (#<PR>)`.
- Bump `<Version>` in `SqlVitals.Desktop.csproj` when a change ships a feature. It reaches users when that version is tagged (see [Release a new version](#release-a-new-version)).
- Document what users see in this wiki: a feature's page, the sidebar, Home's feature map and the reference pages it touches. The README stays a short front page (what SqlVitals is, features in a line each, getting started, how it compares, links here); only touch it when one of those changes.
- Put pure logic in **Engine** and cover it with tests. Script generators, `RowFilter`, `DelimitedText`, `QueryRegressionDetector`, `HistoryDetailTracker`, `SpTraceXmlParser` and `ProcedureStatsDelta` are all database-free and unit-tested. Keep the WPF layer thin.

---

## Add a new screen: step by step

### 1. Write and test the query

Draft it in SSMS or Azure Data Studio. Make it work on both on-premises and Azure SQL Database (see [Azure branching](Architecture.md#azure-sql-database-branching)). Avoid reserved words as aliases (`ROWCOUNT`, `ROWS`, `TABLE`, `KEY`, `INDEX`, `VALUE`).

### 2. Add a model

`SqlVitals/Engine/Models/YourFeature.cs`:

```csharp
namespace SqlVitals.Engine.Models;

public record YourFeatureRow(
    string  SomeColumn,
    int     AnotherColumn,
    decimal ValueMB
);
```

### 3. Add the interface method

`SqlVitals/Engine/Repositories/IWaitStatsRepository.cs`:

```csharp
Task<IEnumerable<YourFeatureRow>> GetYourFeatureAsync();
```

### 4. Implement it

In `WaitStatsRepository.cs`:

```csharp
// ── Your feature ──────────────────────────────────────────────────
public async Task<IEnumerable<YourFeatureRow>> GetYourFeatureAsync()
{
    const string sql = """
        SELECT some_column AS SomeColumn,
               another_col AS AnotherColumn,
               CAST(value_pages * 8.0 / 1024 AS DECIMAL(18,2)) AS ValueMB
        FROM sys.some_dmv
        ORDER BY ValueMB DESC
        """;

    using var conn = CreateConnection();
    return (await Q(conn, sql)).Select(r => new YourFeatureRow(
        (string)r.SomeColumn,
        Convert.ToInt32(r.AnotherColumn),
        Convert.ToDecimal(r.ValueMB)));
}
```

> Always `Q(conn, sql)`, never `conn.QueryAsync`. Always `Convert.To*` for numbers, never casts or `?? 0`. See [DBNull-safe mapping](Architecture.md#dbnull-safe-mapping).

### 5. Create the page

`SqlVitals/Desktop/Pages/YourFeaturePage.xaml`:

```xml
<Page x:Class="SqlVitals.Desktop.Pages.YourFeaturePage"
      xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>
        <TextBlock Grid.Row="0" Text="Your Feature" Style="{StaticResource SectionHeader}" />
        <DataGrid x:Name="MainGrid" Grid.Row="1" AutoGenerateColumns="False" CanUserAddRows="False">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Name"     Binding="{Binding SomeColumn}" Width="200"/>
                <DataGridTextColumn Header="Value MB" Binding="{Binding ValueMB, StringFormat=N2}" Width="100"/>
            </DataGrid.Columns>
        </DataGrid>
    </Grid>
</Page>
```

`YourFeaturePage.xaml.cs`:

```csharp
using SqlVitals.Engine.Repositories;

namespace SqlVitals.Desktop.Pages;

public partial class YourFeaturePage : System.Windows.Controls.Page, IRefreshable
{
    private readonly IWaitStatsRepository _repo;

    public YourFeaturePage(IWaitStatsRepository repo)
    {
        _repo = repo;
        InitializeComponent();
    }

    public async Task RefreshAsync()
    {
        MainGrid.ItemsSource = (await _repo.GetYourFeatureAsync()).ToList();
    }
}
```

To keep the sort and filter across refreshes, set rows with `DataGridRefresh.SetItemsSource` and add a `GridFilterBox` (see [Working with Grids](Working-with-Grids.md#under-the-hood)). Use theme brush keys, not hex colours.

### 6. Wire up navigation

`MainWindow.xaml`, in the sidebar `StackPanel`:

```xml
<Button x:Name="BtnYourFeature" Tag="YourFeature"
        Content="✨  Your Feature"
        Style="{StaticResource NavButton}"
        Click="Nav_Click" />
```

`MainWindow.xaml.cs`, in three places:

```csharp
// 1. The reset array
foreach (var btn in new[] { ..., BtnYourFeature })
    btn.Style = (Style)FindResource("NavButton");

// 2. The 'active' switch
"YourFeature" => BtnYourFeature,

// 3. The 'page' switch
"YourFeature" => new YourFeaturePage(Repo),
```

If the page has nothing to show on Azure SQL Database, also add it to `PagesAzureSqlLacks` with the reason. Its button is then disabled on such a connection, dimmed and marked **N/A** by the `NavButton` style, with the reason as its tooltip.

### 7. Add it to Export (optional)

In `ExportService.cs`:

```csharp
public const string G_YOUR_FEATURE = "Your Feature";
public static readonly string[] AllGroups = [ ..., G_YOUR_FEATURE ];

// In ExportAsync():
if (groups.Contains(G_YOUR_FEATURE)) tasks.Add((G_YOUR_FEATURE, FetchYourFeature()));

private async Task<string> FetchYourFeature()
{
    var rows = (await repo.GetYourFeatureAsync()).ToList();
    var sb = new StringBuilder();
    sb.AppendLine($"## Your Feature ({rows.Count} rows)");
    foreach (var r in rows)
        sb.AppendLine($"  {r.SomeColumn} | {r.ValueMB:N2} MB");
    return sb.ToString();
}
```

Then add a description for it to the `_descriptions` dictionary in `ExportPage.xaml.cs`.

### 8. Test and document

Add unit tests for any pure logic, run `dotnet test`, and document the screen in this wiki (its own page, the sidebar, [Home](Home.md)'s feature map, and [Architecture](Architecture.md)'s main flows).

---

## Common development errors

| Error | Cause | Fix |
|---|---|---|
| Build: *file locked by SqlVitals.Desktop* | The app is running | Kill it (see above) |
| `Invalid object name 'sys.master_files'` | Azure SQL Database | Add the `EngineEdition = 5` branch |
| `Cannot convert null to 'decimal'` | `DBNull` from Dapper | `Convert.ToDecimal(r.Col)` |
| `Error converting data type nvarchar to bigint` | A `sql_variant` column holding text | `CASE WHEN ISNUMERIC(CAST(value AS NVARCHAR(256))) = 1 THEN CAST(CAST(value AS NVARCHAR(256)) AS BIGINT) ELSE 0 END` |
| `Incorrect syntax near 'RowCount'` | Reserved word as an alias | Rename the alias |
| `Incorrect syntax near 'THEN'` | `CASE WHEN EXISTS (…)` in a GROUP BY, or an alias in `ORDER BY` after `GROUP BY` on Azure SQL | Use `LEFT JOIN` + `MAX(CASE …)`; order by the full expression |
| IDE: "type not found" for Models | Stale IntelliSense cache | Run `dotnet build`; it will report 0 errors |
