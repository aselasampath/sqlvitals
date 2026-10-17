<#
.SYNOPSIS
    Builds SqlVitals Setup: artifacts\SqlVitals-Setup-<version>.exe
    and the portable zip:   artifacts\SqlVitals-<version>-win-x64-portable.zip
    and the collector zip:  artifacts\SqlVitals-Collector-<version>-win-x64.zip

.DESCRIPTION
    1. Publishes SqlVitals.Desktop self-contained for win-x64 (the .NET 10 runtime is bundled,
       so users don't install anything else).
    2. Zips the published app into a SqlVitals-<version> folder for users who don't want to run
       Setup: unzip anywhere and run SqlVitals.Desktop.exe.
    3. Writes manifest.txt with the size and SHA-256 hash of every published file. Setup checks
       each file against it after unpacking and before installing.
    4. Zips manifest + app into SqlVitals.payload.zip and embeds it in SqlVitals.Setup.exe.
    5. Publishes SqlVitals.Collector, the optional Windows service (#172), self-contained, and
       zips it into a SqlVitals-Collector-<version> folder. Setup doesn't install it: an admin
       copies it to the always-on PC and runs SqlVitals.Collector.exe install.
    6. Optionally Authenticode-signs the app, the collector and Setup (recommended for any release build, so
       Windows shows a verified publisher and the package can't be altered undetected).

.PARAMETER CertificateThumbprint
    Thumbprint of a code-signing certificate in the current user's store. Omit for unsigned dev builds.

.PARAMETER SkipPortable
    Don't build the portable zip (step 2). PR builds pass it: they upload Setup only, and the zip
    holds the same app (#230).

.EXAMPLE
    .\SqlVitals\Installer\Build-Installer.ps1
.EXAMPLE
    .\SqlVitals\Installer\Build-Installer.ps1 -CertificateThumbprint 0123ABCD...
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $CertificateThumbprint,
    [string] $TimestampUrl = 'http://timestamp.digicert.com',
    [switch] $SkipPortable
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repoRoot    = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$artifacts   = Join-Path $repoRoot 'artifacts'
$publishDir  = Join-Path $artifacts 'publish\app'
$payloadDir  = Join-Path $artifacts 'payload'
$payloadRoot = Join-Path $payloadDir 'root'
$payloadZip  = Join-Path $payloadDir 'SqlVitals.payload.zip'
$setupBuild  = Join-Path $artifacts 'setup-build'
$portableDir = Join-Path $artifacts 'portable'
$desktopProj = Join-Path $repoRoot 'SqlVitals\Desktop\SqlVitals.Desktop.csproj'
$collectorProj = Join-Path $repoRoot 'SqlVitals\Collector\SqlVitals.Collector.csproj'
$collectorDir  = Join-Path $artifacts 'publish\collector'
$setupProj   = Join-Path $PSScriptRoot 'SqlVitals.Installer.csproj'

# Config files the user may edit; upgrades keep a modified copy (see CommitFilesStep).
$preserveIfModified = @('appsettings.json')

function Invoke-Checked([string] $what, [scriptblock] $action) {
    Write-Host "==> $what" -ForegroundColor Cyan
    & $action
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

function Invoke-Sign([string[]] $files) {
    if (-not $CertificateThumbprint) { return }
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) { throw 'signtool.exe not found. Install the Windows SDK or add it to PATH.' }
    Invoke-Checked "Signing $($files.Count) file(s)" {
        & $signtool.Source sign /sha1 $CertificateThumbprint /fd SHA256 /tr $TimestampUrl /td SHA256 $files
    }
}

# ── Version comes from the desktop project, so Setup and app always agree ───────
[xml] $desktopXml = Get-Content $desktopProj
$version = @($desktopXml.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ })[0]
if (-not $version) { throw "No <Version> found in $desktopProj." }
Write-Host "SqlVitals version $version" -ForegroundColor Green

foreach ($dir in @($publishDir, $collectorDir, $payloadDir, $setupBuild, $portableDir)) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}

# ── 1. Publish the app ────────────────────────────────────────────────────────
Invoke-Checked 'Publishing SqlVitals.Desktop (self-contained, win-x64)' {
    dotnet publish $desktopProj -c $Configuration -r win-x64 --self-contained true `
        -p:SatelliteResourceLanguages=en -p:DebugType=none -p:DebugSymbols=false `
        -o $publishDir --nologo
}

Invoke-Sign (Get-ChildItem $publishDir -File | Where-Object { $_.Name -like 'SqlVitals.*' -and $_.Extension -in '.exe', '.dll' } |
             ForEach-Object FullName)

# ── 2. Portable zip ───────────────────────────────────────────────────────────
# The zip holds one SqlVitals-<version> folder, so unzipping never scatters files.
# Removed first even when skipped, so an older build's zip never passes for this one.
$portableName = "SqlVitals-$version"
$portableZip  = Join-Path $artifacts "$portableName-win-x64-portable.zip"
foreach ($file in @($portableZip, "$portableZip.sha256")) {
    if (Test-Path $file) { Remove-Item $file -Force }
}
if (-not $SkipPortable) {
    Write-Host '==> Zipping portable app' -ForegroundColor Cyan
    $portableRoot = Join-Path $portableDir $portableName
    New-Item -ItemType Directory -Force $portableDir | Out-Null
    Copy-Item $publishDir $portableRoot -Recurse

    [System.IO.Compression.ZipFile]::CreateFromDirectory($portableRoot, $portableZip,
        [System.IO.Compression.CompressionLevel]::Optimal, $true)
    Remove-Item $portableDir -Recurse -Force

    $portableHash = (Get-FileHash $portableZip -Algorithm SHA256).Hash.ToLowerInvariant()
    Set-Content -Path "$portableZip.sha256" -Value "$portableHash  $(Split-Path $portableZip -Leaf)" -Encoding ascii
}

# ── 3. Manifest ───────────────────────────────────────────────────────────────
Write-Host '==> Writing manifest' -ForegroundColor Cyan
$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('# SqlVitals payload manifest')
$lines.Add("version`t$version")

$files = Get-ChildItem $publishDir -Recurse -File | Sort-Object FullName
foreach ($file in $files) {
    $relative = $file.FullName.Substring($publishDir.Length + 1).Replace('\', '/')
    $hash     = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $flags    = if ($preserveIfModified -contains $relative) { 'preserve' } else { '-' }
    $lines.Add("file`t$hash`t$($file.Length)`t$flags`t$relative")
}

New-Item -ItemType Directory -Force $payloadRoot | Out-Null
[System.IO.File]::WriteAllText((Join-Path $payloadRoot 'manifest.txt'),
    ($lines -join "`n") + "`n", [System.Text.UTF8Encoding]::new($false))
Copy-Item $publishDir (Join-Path $payloadRoot 'app') -Recurse

# ── 4. Payload zip ────────────────────────────────────────────────────────────
Write-Host '==> Compressing payload' -ForegroundColor Cyan
[System.IO.Compression.ZipFile]::CreateFromDirectory($payloadRoot, $payloadZip,
    [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item $payloadRoot -Recurse -Force

$totalMb = ($files | Measure-Object Length -Sum).Sum / 1MB
$zipMb   = (Get-Item $payloadZip).Length / 1MB
Write-Host ("    {0} files, {1:N0} MB -> {2:N0} MB compressed" -f $files.Count, $totalMb, $zipMb)

# ── 5. Collector zip (#172) ───────────────────────────────────────────────────
Invoke-Checked 'Publishing SqlVitals.Collector (self-contained, win-x64)' {
    dotnet publish $collectorProj -c $Configuration -r win-x64 --self-contained true `
        -p:SatelliteResourceLanguages=en -p:DebugType=none -p:DebugSymbols=false `
        -o $collectorDir --nologo
}
Invoke-Sign (Get-ChildItem $collectorDir -File | Where-Object { $_.Name -like 'SqlVitals.*' -and $_.Extension -in '.exe', '.dll' } |
             ForEach-Object FullName)

Write-Host '==> Zipping the collector' -ForegroundColor Cyan
$collectorName = "SqlVitals-Collector-$version"
$collectorRoot = Join-Path $portableDir $collectorName
New-Item -ItemType Directory -Force $portableDir | Out-Null
Copy-Item $collectorDir $collectorRoot -Recurse
$collectorZip = Join-Path $artifacts "$collectorName-win-x64.zip"
if (Test-Path $collectorZip) { Remove-Item $collectorZip -Force }
[System.IO.Compression.ZipFile]::CreateFromDirectory($collectorRoot, $collectorZip,
    [System.IO.Compression.CompressionLevel]::Optimal, $true)
Remove-Item $portableDir -Recurse -Force
$collectorHash = (Get-FileHash $collectorZip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$collectorZip.sha256" -Value "$collectorHash  $(Split-Path $collectorZip -Leaf)" -Encoding ascii

# ── 6. Setup ──────────────────────────────────────────────────────────────────
Invoke-Checked 'Building SqlVitals Setup' {
    dotnet build $setupProj -c $Configuration --no-incremental "-p:PayloadPath=$payloadZip" "-p:Version=$version" `
        -o $setupBuild --nologo
}

$setupExe = Join-Path $artifacts "SqlVitals-Setup-$version.exe"
Copy-Item (Join-Path $setupBuild 'SqlVitals.Setup.exe') $setupExe -Force
Invoke-Sign @($setupExe)

$setupHash = (Get-FileHash $setupExe -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$setupExe.sha256" -Value "$setupHash  $(Split-Path $setupExe -Leaf)" -Encoding ascii

Write-Host ''
Write-Host "Done: $setupExe" -ForegroundColor Green
Write-Host "SHA-256: $setupHash"
if (-not $SkipPortable) {
    Write-Host "Done: $portableZip" -ForegroundColor Green
    Write-Host "SHA-256: $portableHash"
}
Write-Host "Done: $collectorZip" -ForegroundColor Green
Write-Host "SHA-256: $collectorHash"
if (-not $CertificateThumbprint) {
    Write-Warning 'Setup is unsigned. Sign release builds (-CertificateThumbprint) so users see a verified publisher.'
}
