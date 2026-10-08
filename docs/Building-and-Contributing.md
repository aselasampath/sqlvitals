# Building and Contributing

*For developers.* Build SqlVitals from source, run the tests, build the installer, release a new version, and add a new screen.

## Prerequisites

- **.NET 10 SDK**. The app, Engine and their tests target .NET 10 (#163); CI builds with the 10.0 SDK.
- **Windows 10/11**. WPF is Windows-only, and the Installer tests run on .NET Framework 4.7.2.
- A SQL Server or Azure SQL to point at, with `VIEW SERVER STATE` / `VIEW DATABASE STATE`.
- Optional: **Visual Studio 2026** (Visual Studio 2022 can't target .NET 10).

## Build, test, run

```powershell
git clone https://github.com/aselasampath/sqlvitals.git
cd sqlvitals

dotnet build SqlVitals.slnx              # all six projects
dotnet test                              # Engine.Tests + Desktop.Tests + Installer.Tests
dotnet run --project SqlVitals\Desktop   # start the app
```

**A warning fails the build** (#164). `Directory.Build.props` sets `TreatWarningsAsErrors`, `AnalysisLevel` *latest-recommended* (the .NET code-quality analyzers) and `EnforceCodeStyleInBuild` for every project, and `.editorconfig` holds the code style. When the build stops on a warning:

- **Fix it.** Most have a quick fix (Ctrl+. in Visual Studio).
- **Or suppress that one finding, next to the code, with the reason:** `[SuppressMessage("Design", "CA1001", Justification = "…")]` on the member, or `#pragma warning disable` / `restore` around the line with a comment. Never in a project file or for the whole codebase.
- `.editorconfig` turns a rule down only when it doesn't fit a whole kind of code, and says why: underscores in test names (`Method_Scenario_Result`), and CA1859's concrete types, which would replace the read-only interfaces the code keeps on purpose. Style rules the code doesn't follow everywhere yet (braces, `var`, expression bodies, collection expressions, primary constructors) are suggestions: the IDE hints, the build doesn't stop.
- The two LiveCharts projects (Desktop and Desktop.Tests) set `NoWarn` for `NU1701`: SkiaSharp.Views.WPF and OpenTK only ship .NET Framework builds, and they run fine on .NET 10.
- NuGet's vulnerability warnings (`NU1901`–`NU1904`) stay warnings: an advisory against a dependency shows in the build, but doesn't stop an unrelated fix from shipping. Update the package in its own PR.

**Coverage**, as CI measures it:

```powershell
.\.github\scripts\Test-WithCoverage.ps1    # all three test projects, with coverlet
# artifacts\coverage\report\SummaryGithub.md: line and branch coverage per class
```

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
Start-Process "SqlVitals\Desktop\bin\Debug\net10.0-windows\SqlVitals.Desktop.exe"
```

**Visual Studio:** open `SqlVitals.slnx`, set `SqlVitals.Desktop` as the startup project, press **F5**.

**Only one SqlVitals runs per Windows session**, whichever exe it is. If an installed copy is running (perhaps hidden in the notification area), starting your dev build just brings the installed copy to the front and exits. Exit the installed copy first. They share `settings.dat` and `history.db`, which is why they can't run side by side.

## Repository layout

```text
SqlVitals.slnx
.github/workflows/pr-setup.yml   CI: tests + coverage + Setup build on every PR to main
.github/workflows/release.yml    Release: tag vX.Y.Z → tests, Setup build, GitHub Release
.github/scripts/Test-WithCoverage.ps1   Both workflows' test step: tests, coverlet, ReportGenerator
Directory.Build.props            Build rules for every project: warnings are errors, analyzers on
.editorconfig                    Code style; which rules fail the build
SqlVitals/
  Desktop/          WPF app: Pages/, ViewModels/, Controls/, Windows/, Helpers/, Services/, Styles/
                    (+ appsettings.json)
  Engine/           Models/, Repositories/, History/, Regressions/, Scripting/, Export/,
                    Filtering/, Monitoring/, Errors/ (a class library)
  Engine.Tests/     xUnit
  Desktop.Tests/    xUnit for the app's services (settings file, monitoring), pages,
                    and the MVVM guard tests over the app's sources
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
2. Runs `Installer.Tests`, `Engine.Tests`, then `Desktop.Tests` (Release) with coverlet, through `.github/scripts/Test-WithCoverage.ps1`. A build warning fails this step.
3. Posts the coverage, line and branch per assembly, as a comment on the PR, and updates that same comment on every later run. The run summary lists every class. Nothing fails on a low number yet.
4. Builds an **unsigned** Setup and portable zip with `Build-Installer.ps1`.
5. Posts the file names, sizes, SHA-256 hashes and commit to the run summary.
6. Uploads `SqlVitals-Setup-<version>-pr<N>` and `SqlVitals-<version>-win-x64-portable-pr<N>` as artifacts, kept for **14 days** (Actions tab → run → *Artifacts*).

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
2. Runs `Installer.Tests`, `Engine.Tests` and `Desktop.Tests` (Release) with coverage, which goes to the run summary.
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
- **Keep a PR under about 800 changed lines** (added plus removed). Split bigger work into PRs that each build and pass on their own, for example the plumbing first and the screens that use it next. A mechanical change (a rename, a reformat) can go over if it's in a PR of its own.
- **Review before merging:** a person, or a `/code-review` pass in Claude Code. Fix what it confirms, with a test, and list the findings in the PR.
- Squash-merge titles follow `<Title> (#<issue>) (#<PR>)`.
- Bump `<Version>` in `SqlVitals.Desktop.csproj` when a change ships a feature. It reaches users when that version is tagged (see [Release a new version](#release-a-new-version)).
- Document what users see in this wiki: a feature's page, the sidebar, Home's feature map and the reference pages it touches. The README stays a short front page (what SqlVitals is, features in a line each, getting started, how it compares, links here); only touch it when one of those changes.
- Follow the MVVM rules in [Architecture](Architecture.md#mvvm). `MvvmGuardTests` names the file and line of a new offence. It also fails an allowlist entry that no longer offends, so when you move a page to its view model, take the page off the allowlists in the same PR.
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

Add it to its area's interface, for example `SqlVitals/Engine/Repositories/IIndexRepository.cs`, and hand it on in `WaitStatsRepository.cs` (`=> _indexes.GetYourFeatureAsync();`). A feature that is an area of its own gets its own `IYourFeatureRepository` and `YourFeatureRepository`: add the interface to `IWaitStatsRepository`'s list and a field to `WaitStatsRepository`.

```csharp
Task<IEnumerable<YourFeatureRow>> GetYourFeatureAsync();
```

### 4. Write the query and implement it

The query is a file of its own, `SqlVitals/Engine/Sql/Index/YourFeature.sql` (the folder is the repository's area), which the build embeds:

```sql
SELECT some_column AS SomeColumn,
       another_col AS AnotherColumn,
       CAST(value_pages * 8.0 / 1024 AS DECIMAL(18,2)) AS ValueMB
FROM sys.some_dmv
ORDER BY ValueMB DESC
```

In `IndexRepository.cs`:

```csharp
// ── Your feature ──────────────────────────────────────────────────
public async Task<IEnumerable<YourFeatureRow>> GetYourFeatureAsync()
{
    using var conn = CreateConnection();
    return (await Q(conn, SqlScripts.Get("Index/YourFeature"))).Select(r => new YourFeatureRow(
        (string)r.SomeColumn,
        Convert.ToInt32(r.AnotherColumn),
        Convert.ToDecimal(r.ValueMB)));
}
```

> A part that depends on the server's version is a `/*MARKER*/` in the file, filled with `Replace` (see [Architecture → Where the SQL lives](Architecture.md#where-the-sql-lives)). `SqlScriptsTests` fails for a name that has no file.
>
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
        <DataGrid x:Name="MainGrid" Grid.Row="1" ItemsSource="{Binding Rows}" AutoGenerateColumns="False" CanUserAddRows="False">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Name"     Binding="{Binding SomeColumn}" Width="200"/>
                <DataGridTextColumn Header="Value MB" Binding="{Binding ValueMB, StringFormat=N2}" Width="100"/>
            </DataGrid.Columns>
        </DataGrid>
    </Grid>
</Page>
```

The page's view model reads the data. It goes in `SqlVitals/Desktop/ViewModels/Pages/YourFeatureViewModel.cs`, one file named after the page (see [Architecture → MVVM](Architecture.md#mvvm)):

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using SqlVitals.Engine.Repositories;
using SqlVitals.Engine.Verdicts;

namespace SqlVitals.Desktop.ViewModels.Pages;

public sealed partial class YourFeatureViewModel(IWaitStatsRepository repo) : PageViewModel
{
    [ObservableProperty]
    private IReadOnlyList<YourFeatureRow> _rows = [];

    // Run when the page opens, on Refresh and F5, and every RefreshInterval if you set one.
    protected override async Task LoadAsync(CancellationToken cancel) => Rows = (await repo.GetYourFeatureAsync()).ToList();

    protected override void OnAction(VerdictAction action) { }
}
```

`YourFeaturePage.xaml.cs` holds only the view. Page code that uses a repository fails `MvvmGuardTests`:

```csharp
using SqlVitals.Desktop.ViewModels.Pages;

namespace SqlVitals.Desktop.Pages;

public partial class YourFeaturePage : System.Windows.Controls.Page, IPageContext
{
    public PageKind Kind => PageKind.Snapshot;

    internal YourFeatureViewModel ViewModel { get; }

    public YourFeaturePage(YourFeatureViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
    }
}
```

The page factory makes the view model the page's `DataContext` and runs it: it loads when the page opens, reads again on Refresh, and is cancelled when the page is left (see [Architecture → The page lifecycle](Architecture.md#the-page-lifecycle)).

Bind buttons to `[RelayCommand]` methods, not `Click=`. Add a `GridFilterBox` to filter the grid (see [Working with Grids](Working-with-Grids.md#under-the-hood)). Replacing `Rows` makes a new collection view, which drops the grid's sort and filter. Until the pilot (#184) settles how a bound grid keeps them, a page that needs them hands the view model's `Rows` to `DataGridRefresh.SetItemsSource` from code-behind instead of binding `ItemsSource`. Use theme brush keys, not hex colours.

### 6. Wire up navigation

The sidebar is built from `Desktop/Services/NavMenu.cs` (#124). Add the page to the group whose question it answers, with an icon no other page uses:

```csharp
new("Queries", "Queries", "Which queries cost the most, which got slower, and why?",
[
    ...
    new("YourFeature", "✨", "Your Feature", "Optional tooltip"),
]),
```

Then add its tag to `Desktop/Services/PageCatalog.cs`, and register it in `Desktop/Services/PageRegistrations.cs`, where the page factory finds it:

```csharp
.AddPage("YourFeature", (_, a) => new YourFeaturePage(new YourFeatureViewModel(a.Repo)), p => p.ViewModel)
```

`Desktop.Tests/NavMenuTests.cs` lists every page and the groups' layout, so update it too, and `PageFactoryTests` creates every page in `PageCatalog` through the factory. A tag `NavigateTo` is given with no button in `NavMenu` fails a `Debug.Assert`.

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

Add unit tests for any pure logic and for the view model, run `dotnet test` (the MVVM guard tests run with it), and document the screen in this wiki (its own page, the sidebar, [Home](Home.md)'s feature map, and [Architecture](Architecture.md)'s main flows).

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
