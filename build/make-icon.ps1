# Renders the app icon (dark rounded tile with six wire bars) at several sizes and packs them into Assets\app.ico.
param([string]$Out = (Join-Path $PSScriptRoot "..\Assets\app.ico"))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$heights = 0.55, 0.78, 0.66, 0.88, 0.50, 0.72

$frames = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    $radius = [Math]::Max(2, [int]($s * 0.22)); $d = $radius * 2; $edge = $s - 1
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90)
    $path.AddArc($edge - $d, 0, $d, $d, 270, 90)
    $path.AddArc($edge - $d, $edge - $d, $d, $d, 0, 90)
    $path.AddArc(0, $edge - $d, $d, $d, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x14, 0x16, 0x1B))), $path)

    $margin = $s * 0.17; $inner = $s - 2 * $margin; $gap = [Math]::Max(1, $inner * 0.07); $bw = ($inner - 5 * $gap) / 6
    $bar = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x3D, 0xD6, 0xA0))
    for ($i = 0; $i -lt 6; $i++) {
        $h = $inner * $heights[$i]
        $x = $margin + $i * ($bw + $gap)
        $g.FillRectangle($bar, [float]$x, [float]($margin + $inner - $h), [float]$bw, [float]$h)
    }
    $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container with PNG-compressed entries (supported since Windows Vista).
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$bw = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($Out))
$bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([uint16]1); $bw.Write([uint16]32)
    $bw.Write([uint32]$frames[$i].Length); $bw.Write([uint32]$offset)
    $offset += $frames[$i].Length
}
foreach ($f in $frames) { $bw.Write($f) }
$bw.Close()
"wrote $Out ($((Get-Item $Out).Length) bytes, $($sizes.Count) sizes)"
