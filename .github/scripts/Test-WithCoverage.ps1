<#
  Runs the three test projects with coverlet and writes a coverage summary (#164).
  Used by both workflows; runs the same way on a dev machine:

    ./.github/scripts/Test-WithCoverage.ps1

  Output, under artifacts/coverage (git-ignored):
    report/SummaryGithub.md  per-class tables, for the run summary
    comment.md               per-assembly table (report/Summary.md), for the PR comment
  Nothing fails on a low number yet; a threshold comes once the numbers are known.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $OutDir = 'artifacts/coverage'
)

$ErrorActionPreference = 'Stop'
Push-Location (Resolve-Path "$PSScriptRoot/../..")
try {

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }

# Installer first: its tests run before the ~80 MB payload exists. Then the engine, the app and the collector service (#172).
$projects = [ordered]@{
    'Installer' = 'SqlVitals/Installer.Tests/SqlVitals.Installer.Tests.csproj'
    'Engine'    = 'SqlVitals/Engine.Tests/SqlVitals.Engine.Tests.csproj'
    'Desktop'   = 'SqlVitals/Desktop.Tests/SqlVitals.Desktop.Tests.csproj'
    'Collector' = 'SqlVitals/Collector.Tests/SqlVitals.Collector.Tests.csproj'
}
foreach ($name in $projects.Keys) {
    Write-Host "::group::Test $name"
    dotnet test $projects[$name] -c $Configuration --collect 'XPlat Code Coverage' --results-directory "$OutDir/$name"
    $failed = $LASTEXITCODE
    Write-Host '::endgroup::'
    if ($failed) { throw "$name tests failed." }
}

# ReportGenerator merges the three Cobertura files; generated XAML code (*.g.cs) isn't ours to cover.
$tools = 'artifacts/tools'
if (-not (Test-Path "$tools/reportgenerator*")) {
    dotnet tool install dotnet-reportgenerator-globaltool --version 5.* --tool-path $tools | Out-Null
    if ($LASTEXITCODE) { throw 'Could not install ReportGenerator.' }
}
& "$tools/reportgenerator" "-reports:$OutDir/*/*/coverage.cobertura.xml" "-targetdir:$OutDir/report" `
    '-reporttypes:MarkdownSummaryGithub;MarkdownAssembliesSummary' '-filefilters:-*.g.cs;-*.g.i.cs' '-verbosity:Warning'
if ($LASTEXITCODE) { throw 'ReportGenerator failed.' }

# The PR comment: one line per assembly, and a marker the workflow finds the comment by.
$assemblies = (Get-Content "$OutDir/report/Summary.md" | Where-Object { $_ -ne '# Summary' }) -join "`n"
@(
    '<!-- sqlvitals-coverage -->'
    '### Test coverage'
    ''
    $assemblies.Trim()
    ''
    "_Line and branch coverage from coverlet, for the commit this PR's last run built. The run summary lists every class._"
) | Set-Content "$OutDir/comment.md" -Encoding utf8

if ($env:GITHUB_STEP_SUMMARY) {
    Get-Content "$OutDir/report/SummaryGithub.md" -Raw | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8
}
Write-Host "Coverage summary: $OutDir/report/SummaryGithub.md"

}
finally { Pop-Location }
