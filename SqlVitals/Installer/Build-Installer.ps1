<#
.SYNOPSIS
    Builds SqlVitals Setup: artifacts\SqlVitals-Setup-<version>.exe

.DESCRIPTION
    1. Publishes SqlVitals.Desktop self-contained for win-x64 (the .NET 8 runtime is bundled,
       so users don't install anything else).
    2. Writes manifest.txt with the size and SHA-256 hash of every published file. Setup checks
       each file against it after unpacking and before installing.
    3. Zips manifest + app into SqlVitals.payload.zip and embeds it in SqlVitals.Setup.exe.
    4. Optionally Authenticode-signs the app and Setup (recommended for any release build, so
       Windows shows a verified publisher and the package can't be altered undetected).

.PARAMETER CertificateThumbprint
    Thumbprint of a code-signing certificate in the current user's store. Omit for unsigned dev builds.

.EXAMPLE
    .\SqlVitals\Installer\Build-Installer.ps1
.EXAMPLE
    .\SqlVitals\Installer\Build-Installer.ps1 -CertificateThumbprint 0123ABCD...
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $CertificateThumbprint,
    [string] $TimestampUrl = 'http://timestamp.digicert.com'
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
$desktopProj = Join-Path $repoRoot 'SqlVitals\Desktop\SqlVitals.Desktop.csproj'
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

foreach ($dir in @($publishDir, $payloadDir, $setupBuild)) {
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

# ── 2. Manifest ───────────────────────────────────────────────────────────────
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

# ── 3. Payload zip ────────────────────────────────────────────────────────────
Write-Host '==> Compressing payload' -ForegroundColor Cyan
[System.IO.Compression.ZipFile]::CreateFromDirectory($payloadRoot, $payloadZip,
    [System.IO.Compression.CompressionLevel]::Optimal, $false)
Remove-Item $payloadRoot -Recurse -Force

$totalMb = ($files | Measure-Object Length -Sum).Sum / 1MB
$zipMb   = (Get-Item $payloadZip).Length / 1MB
Write-Host ("    {0} files, {1:N0} MB -> {2:N0} MB compressed" -f $files.Count, $totalMb, $zipMb)

# ── 4. Setup ──────────────────────────────────────────────────────────────────
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
if (-not $CertificateThumbprint) {
    Write-Warning 'Setup is unsigned. Sign release builds (-CertificateThumbprint) so users see a verified publisher.'
}
