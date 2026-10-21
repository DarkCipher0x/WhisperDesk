# Generates the WhisperDesk logo: the darkcipher0x cipher emblem (ring, segmented arc, hood)
# fused with "whisper" sound waves in place of the circuit nodes. Brand teal #36D2CB on black.
# Outputs: Assets\app.ico (16-256 px), Assets\logo.png (512, transparent), Assets\logo-tile.png (1024, black tile)
param([string]$OutDir = (Join-Path $PSScriptRoot "..\Assets"))
Add-Type -AssemblyName System.Drawing
$ErrorActionPreference = 'Stop'

$teal     = [System.Drawing.Color]::FromArgb(255, 0x36, 0xD2, 0xCB)
$tealMid  = [System.Drawing.Color]::FromArgb(255, 0x20, 0xA0, 0xA0)
$tealDeep = [System.Drawing.Color]::FromArgb(255, 0x0E, 0x4A, 0x4A)

function Pen($color, $width, $alpha = 255) {
    $p = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb($alpha, $color.R, $color.G, $color.B)), $width
    $p.StartCap = 'Round'; $p.EndCap = 'Round'; $p.LineJoin = 'Round'
    return $p
}

# Draws a path several times with widening, fading pens for a neon glow.
function Glow($g, $path, $color, $width, $s) {
    if ($s -ge 48) {
        $k = $s / 256.0
        foreach ($pass in @(@(10, 22), @(6, 40), @(3, 70))) { $g.DrawPath((Pen $color ($width + $pass[0] * $k) $pass[1]), $path) }
    }
    $g.DrawPath((Pen $color $width), $path)
}

function Draw-Logo([int]$size, [bool]$tile) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.Clear([System.Drawing.Color]::Transparent)
    $k = $size / 256.0   # design grid is 256 x 256
    $s = $size

    if ($tile) {
        # rounded black tile with a soft teal radial glow
        $r = 56 * $k
        $tilePath = New-Object System.Drawing.Drawing2D.GraphicsPath
        $tilePath.AddArc(0, 0, $r, $r, 180, 90); $tilePath.AddArc($s - $r - 1, 0, $r, $r, 270, 90)
        $tilePath.AddArc($s - $r - 1, $s - $r - 1, $r, $r, 0, 90); $tilePath.AddArc(0, $s - $r - 1, $r, $r, 90, 90); $tilePath.CloseFigure()
        $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 4, 8, 8))), $tilePath)
        $glow = New-Object System.Drawing.Drawing2D.PathGradientBrush $tilePath
        $glow.CenterColor = [System.Drawing.Color]::FromArgb(110, $tealMid.R, $tealMid.G, $tealMid.B)
        $glow.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 0, 0, 0))
        $g.FillPath($glow, $tilePath)
    }

    $w = [math]::Max(1.6, 7 * $k)          # main stroke width

    # 1. Outer ring
    $ring = New-Object System.Drawing.Drawing2D.GraphicsPath
    $ring.AddEllipse(20 * $k, 20 * $k, 216 * $k, 216 * $k)
    Glow $g $ring $teal $w $s

    # 2. Segmented arc (top-left, like the cipher emblem) - skipped at tiny sizes
    if ($size -ge 32) {
        $segPen = Pen $tealMid ([math]::Max(2, 13 * $k))
        $segPen.StartCap = 'Flat'; $segPen.EndCap = 'Flat'
        for ($a = 150; $a -le 285; $a += 15) { $g.DrawArc($segPen, 42 * $k, 42 * $k, 172 * $k, 172 * $k, $a, 11) }
    }

    # 3. Terminal computer: retro monitor on a stand
    $screenTop = 66; $screenBottom = 168
    $mon = New-Object System.Drawing.Drawing2D.GraphicsPath
    $rr = 14 * $k; $x0 = 54 * $k; $y0 = $screenTop * $k; $x1 = 202 * $k; $y1 = $screenBottom * $k
    $mon.AddArc($x0, $y0, $rr, $rr, 180, 90); $mon.AddArc($x1 - $rr, $y0, $rr, $rr, 270, 90)
    $mon.AddArc($x1 - $rr, $y1 - $rr, $rr, $rr, 0, 90); $mon.AddArc($x0, $y1 - $rr, $rr, $rr, 90, 90); $mon.CloseFigure()
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush (New-Object System.Drawing.PointF 0, $y0), (New-Object System.Drawing.PointF 0, $y1), ([System.Drawing.Color]::FromArgb(255, 0x06, 0x24, 0x24)), ([System.Drawing.Color]::FromArgb(255, 0x01, 0x08, 0x08))
    $g.FillPath($fill, $mon)
    Glow $g $mon $teal $w $s

    # stand: neck + base
    $standPen = Pen $teal $w
    $g.DrawLine($standPen, 128 * $k, ($screenBottom + 4) * $k, 128 * $k, 186 * $k)
    $g.DrawLine($standPen, 96 * $k, 190 * $k, 160 * $k, 190 * $k)

    # title bar with three dots (detailed sizes only)
    if ($size -ge 48) {
        $g.DrawLine((Pen $tealMid ([math]::Max(1, 3 * $k)) 220), 62 * $k, 86 * $k, 194 * $k, 86 * $k)
        $dot = New-Object System.Drawing.SolidBrush $tealMid
        foreach ($dx in 70, 82, 94) { $g.FillEllipse($dot, ($dx - 3.5) * $k, (76 - 3.5) * $k, 7 * $k, 7 * $k) }
    }

    # prompt  >_
    $p = Pen $teal ([math]::Max(1.8, 7 * $k))
    $g.DrawLines($p, [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF (70 * $k), (104 * $k)),
        (New-Object System.Drawing.PointF (86 * $k), (117 * $k)),
        (New-Object System.Drawing.PointF (70 * $k), (130 * $k))))
    $g.DrawLine($p, 94 * $k, 131 * $k, 110 * $k, 131 * $k)   # cursor

    # whisper: voice waveform on screen…
    if ($size -ge 24) {
        $bars = @(10, 22, 34, 18, 28, 12, 6)
        $bx = 124
        foreach ($h in $bars) {
            $g.DrawLine((Pen $teal ([math]::Max(1.4, 5.5 * $k))), $bx * $k, (117 - $h / 2) * $k, $bx * $k, (117 + $h / 2) * $k)
            $bx += 10
        }
    }
    # …turning into lines of transcript text
    if ($size -ge 48) {
        $line = Pen $tealMid ([math]::Max(1.4, 5 * $k)) 230
        $g.DrawLine($line, 70 * $k, 148 * $k, 150 * $k, 148 * $k)
        $g.DrawLine($line, 160 * $k, 148 * $k, 186 * $k, 148 * $k)
    }

    $g.Dispose()
    return $bmp
}

function Save-Png($bmp, $path) { $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png) }

New-Item -ItemType Directory -Force $OutDir | Out-Null
$logo = Draw-Logo 512 $false; Save-Png $logo (Join-Path $OutDir "logo.png"); $logo.Dispose()
$tile = Draw-Logo 1024 $true;  Save-Png $tile (Join-Path $OutDir "logo-tile.png"); $tile.Dispose()

# Multi-size .ico (PNG-compressed entries)
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$pngs = foreach ($sz in $sizes) {
    $b = Draw-Logo $sz $true; $ms = New-Object System.IO.MemoryStream; $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $b.Dispose(); ,$ms.ToArray()
}
$out = New-Object System.IO.MemoryStream; $w = New-Object System.IO.BinaryWriter $out
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $d = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$d); $w.Write([byte]$d); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $w.Write($p) }
$w.Flush(); [System.IO.File]::WriteAllBytes((Join-Path $OutDir "app.ico"), $out.ToArray())
"Wrote logo.png, logo-tile.png, app.ico to $OutDir"


