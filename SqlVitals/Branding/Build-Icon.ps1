<#
.SYNOPSIS
    Generates the SqlVitals icon: SqlVitals.ico (16–256 px), sqlvitals.svg and PNG previews.

.DESCRIPTION
    The design is a database cylinder with a heartbeat line across it ("SQL" + "vitals") on a
    rounded square in the app's accent purple. This script holds the only copy of the geometry,
    on a 256-unit grid, and renders it with WPF, so no image tools are needed. Small sizes use a
    thicker, simpler pulse so it stays readable at 16 px.

    Run with Windows PowerShell (WPF):  powershell -STA -File .\SqlVitals\Branding\Build-Icon.ps1
#>
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$outDir = $PSScriptRoot

# ── Design (256 × 256 grid) ─────────────────────────────────────────────────────
$bgTop      = '#8B5CF6'   # violet-500
$bgBottom   = '#5B21B6'   # violet-800 — the app accent #7C3AED sits between the two
$corner     = 56
$cylFront   = '#FFFFFF'
$cylTop     = '#DDD6FE'   # violet-200: separates the lid from the body
$pulseColor = '#6D28D9'   # violet-700

# Geometry per size. At 24 px and below (Explorer, title bars, the notification area) the
# cylinder fills more of the square and the heartbeat becomes one bold spike, because the
# full waveform blurs into noise at that size.
function Get-Design([int] $size) {
    if ($size -le 24) {
        return @{
            Body  = 'M 40 66 V 190 A 88 28 0 0 0 216 190 V 66 Z'
            Lid   = @{ Cx = 128; Cy = 66; Rx = 88; Ry = 28 }
            Pulse = 'M 40 150 H 100 L 128 100 L 156 150 H 216'
            Width = 32
        }
    }
    return @{
        Body  = 'M 60 70 V 186 A 68 24 0 0 0 196 186 V 70 Z'
        Lid   = @{ Cx = 128; Cy = 70; Rx = 68; Ry = 24 }
        Pulse = 'M 60 136 H 96 L 110 112 L 128 168 L 146 100 L 160 136 H 196'
        Width = $(if ($size -le 48) { 18 } else { 14 })
    }
}

# ── SVG (for docs, web favicon, marketing) ───────────────────────────────────────
$d = Get-Design 256
$body = $d.Body; $lidCx = $d.Lid.Cx; $lidCy = $d.Lid.Cy; $lidRx = $d.Lid.Rx; $lidRy = $d.Lid.Ry
$pulse = @{ Path = $d.Pulse; Width = $d.Width }
$svg = @"
<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" role="img" aria-label="SqlVitals">
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="$bgTop"/>
      <stop offset="1" stop-color="$bgBottom"/>
    </linearGradient>
    <clipPath id="body"><path d="$body"/></clipPath>
  </defs>
  <rect width="256" height="256" rx="$corner" fill="url(#bg)"/>
  <path d="$body" fill="$cylFront"/>
  <ellipse cx="$lidCx" cy="$lidCy" rx="$lidRx" ry="$lidRy" fill="$cylTop"/>
  <path d="$($pulse.Path)" fill="none" stroke="$pulseColor" stroke-width="$($pulse.Width)"
        stroke-linecap="round" stroke-linejoin="round" clip-path="url(#body)"/>
</svg>
"@
[IO.File]::WriteAllText((Join-Path $outDir 'sqlvitals.svg'), $svg, [Text.UTF8Encoding]::new($false))

# ── WPF rendering ─────────────────────────────────────────────────────────────────
function Brush([string] $hex) { [Windows.Media.SolidColorBrush][Windows.Media.ColorConverter]::ConvertFromString($hex) }

function Render-Icon([int] $size) {
    $visual = New-Object Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $dc.PushTransform((New-Object Windows.Media.ScaleTransform ($size / 256.0), ($size / 256.0)))

    $grad = New-Object Windows.Media.LinearGradientBrush ([Windows.Media.ColorConverter]::ConvertFromString($bgTop)),
                                                        ([Windows.Media.ColorConverter]::ConvertFromString($bgBottom)), 45.0
    $dc.DrawRoundedRectangle($grad, $null, (New-Object Windows.Rect 0, 0, 256, 256), $corner, $corner)

    $d = Get-Design $size
    $bodyGeo = [Windows.Media.Geometry]::Parse($d.Body)
    $dc.DrawGeometry((Brush $cylFront), $null, $bodyGeo)
    $dc.DrawEllipse((Brush $cylTop), $null, (New-Object Windows.Point $d.Lid.Cx, $d.Lid.Cy), $d.Lid.Rx, $d.Lid.Ry)

    $pen = New-Object Windows.Media.Pen (Brush $pulseColor), $d.Width
    $pen.StartLineCap = 'Round'; $pen.EndLineCap = 'Round'; $pen.LineJoin = 'Round'
    $dc.PushClip($bodyGeo)
    $dc.DrawGeometry($null, $pen, [Windows.Media.Geometry]::Parse($d.Pulse))
    $dc.Pop()

    $dc.Pop()
    $dc.Close()

    $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap $size, $size, 96, 96, ([Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($visual)
    return $bmp
}

function Get-Png($bitmap) {
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    return ,$ms.ToArray()   # comma keeps PowerShell from unrolling the byte array
}

# Classic 32-bit DIB entry (what every Windows component reads), bottom-up BGRA + empty AND mask.
function Get-Dib($bitmap, [int] $size) {
    $converted = New-Object Windows.Media.Imaging.FormatConvertedBitmap $bitmap, ([Windows.Media.PixelFormats]::Bgra32), $null, 0
    $stride = $size * 4
    $pixels = New-Object byte[] ($stride * $size)
    $converted.CopyPixels($pixels, $stride, 0)

    $maskStride = [int]([Math]::Ceiling($size / 32.0) * 4)
    $ms = New-Object IO.MemoryStream
    $w  = New-Object IO.BinaryWriter $ms
    $w.Write([int]40); $w.Write([int]$size); $w.Write([int]($size * 2))
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]0)
    $w.Write([int]($stride * $size + $maskStride * $size)); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
    for ($y = $size - 1; $y -ge 0; $y--) { $w.Write($pixels, $y * $stride, $stride) }
    $w.Write((New-Object byte[] ($maskStride * $size)))
    $w.Flush()
    return ,$ms.ToArray()
}

# ── ICO: DIB entries up to 128 px, PNG for 256 px (standard since Windows Vista) ──
$sizes   = 16, 20, 24, 32, 40, 48, 64, 128, 256
$entries = foreach ($s in $sizes) {
    $bmp = Render-Icon $s
    [pscustomobject]@{ Size = $s; Data = [byte[]]$(if ($s -ge 256) { Get-Png $bmp } else { Get-Dib $bmp $s }) }
}

$ico = New-Object IO.MemoryStream
$w   = New-Object IO.BinaryWriter $ico
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$entries.Count)
$offset = 6 + 16 * $entries.Count
foreach ($e in $entries) {
    $dim = if ($e.Size -ge 256) { 0 } else { $e.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$e.Data.Length); $w.Write([int]$offset)
    $offset += $e.Data.Length
}
foreach ($e in $entries) { $w.Write($e.Data) }
$w.Flush()
[IO.File]::WriteAllBytes((Join-Path $outDir 'SqlVitals.ico'), $ico.ToArray())

# ── PNGs for docs / store listings ──────────────────────────────────────────────
foreach ($s in 256, 512) {
    [IO.File]::WriteAllBytes((Join-Path $outDir "sqlvitals-$s.png"), (Get-Png (Render-Icon $s)))
}

Write-Host "Wrote SqlVitals.ico ($($sizes -join ', ') px), sqlvitals.svg, sqlvitals-256.png, sqlvitals-512.png to $outDir"
