# Generates src/UsbLanPrinterBridge/app.ico (a printer glyph) with PNG-compressed 16/24/32/48/64/128/256 px entries.
# Windows Vista+ (so Windows 8 and later) read PNG entries in .ico files natively.
param(
    [string]$OutFile = (Join-Path $PSScriptRoot "..\src\UsbLanPrinterBridge\app.ico")
)

Add-Type -AssemblyName System.Drawing

function Draw-Printer([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 32.0

    $body   = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 45, 62, 80))
    $bodyHi = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 70, 92, 115))
    $paper  = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 250, 250, 250))
    $ink    = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 150, 160, 170))
    $led    = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 76, 200, 90))
    $lan    = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 30, 144, 255))

    # incoming paper (top)
    $g.FillRectangle($paper, 9*$s, 2*$s, 14*$s, 10*$s)
    $g.FillRectangle($ink, 11*$s, 4*$s, 10*$s, 1.2*$s)
    $g.FillRectangle($ink, 11*$s, 6.5*$s, 7*$s, 1.2*$s)

    # body
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 3*$s; $x = 3*$s; $y = 11*$s; $w = 26*$s; $h = 13*$s
    $path.AddArc($x, $y, $r, $r, 180, 90)
    $path.AddArc($x + $w - $r, $y, $r, $r, 270, 90)
    $path.AddArc($x + $w - $r, $y + $h - $r, $r, $r, 0, 90)
    $path.AddArc($x, $y + $h - $r, $r, $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath($body, $path)
    $g.FillRectangle($bodyHi, 3*$s, 11*$s, 26*$s, 3*$s)

    # receipt coming out (bottom)
    $g.FillRectangle($paper, 9*$s, 20*$s, 14*$s, 10*$s)
    $g.FillRectangle($ink, 11*$s, 23*$s, 10*$s, 1.2*$s)
    $g.FillRectangle($ink, 11*$s, 26*$s, 6*$s, 1.2*$s)

    # LAN badge + LED
    $g.FillEllipse($lan, 21*$s, 14*$s, 6*$s, 6*$s)
    $g.FillEllipse($led, 5*$s, 15.5*$s, 3*$s, 3*$s)

    $g.Dispose()
    return $bmp
}

$sizes = 16, 24, 32, 48, 64, 128, 256
$blobs = @()
foreach ($sz in $sizes) {
    $bmp = Draw-Printer $sz
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $blobs += ,($ms.ToArray())
    $bmp.Dispose(); $ms.Dispose()
}

$dir = Split-Path -Parent $OutFile
if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
$fs = [System.IO.File]::Create($OutFile)
$bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]($(if ($sz -ge 256) { 0 } else { $sz })))
    $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32)
    $bw.Write([UInt32]$blobs[$i].Length)
    $bw.Write([UInt32]$offset)
    $offset += $blobs[$i].Length
}
foreach ($b in $blobs) { $bw.Write($b) }
$bw.Flush(); $bw.Dispose(); $fs.Dispose()
Write-Output "Wrote $OutFile ($((Get-Item $OutFile).Length) bytes)"
